﻿namespace Simple.Web
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.ComponentModel.Composition;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using CodeGeneration;
    using Helpers;
    using Hosting;
    using Http;
    using Owin;
    using Routing;
#pragma warning disable 811
    using BodyDelegate = System.Func<System.IO.Stream, System.Threading.CancellationToken, System.Threading.Tasks.Task>;
    using ResponseHandler = System.Func<int, System.Collections.Generic.IDictionary<string, string[]>, System.Func<System.IO.Stream, System.Threading.CancellationToken, System.Threading.Tasks.Task>, System.Threading.Tasks.Task>;
    using App = System.Func<System.Collections.Generic.IDictionary<string, object>, System.Collections.Generic.IDictionary<string, string[]>, System.IO.Stream, System.Threading.CancellationToken, System.Func<int, System.Collections.Generic.IDictionary<string, string[]>, System.Func<System.IO.Stream, System.Threading.CancellationToken, System.Threading.Tasks.Task>, System.Threading.Tasks.Task>, System.Delegate, System.Threading.Tasks.Task>;
#pragma warning restore 811

    /// <summary>
    /// The running application.
    /// </summary>
    public class Application
    {
        private static readonly object StartupLock = new object();
        private static volatile StartupTaskRunner _startupTaskRunner = new StartupTaskRunner();

        /// <summary>
        /// The OWIN standard application method.
        /// </summary>
        /// <param name="env">The OWIN Environment dictionary.</param>
        /// <param name="headers">The headers.</param>
        /// <param name="body">The request input <see cref="Stream"/>.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="responseHandler">The OWIN response handler delegate.</param>
        /// <param name="next">The next OWIN App in the chain.</param>
        /// <returns>A <see cref="Task"/> which will complete the request.</returns>
        [Export("Owin.Application")]
        public static Task Run(IDictionary<string, object> env, IDictionary<string, string[]> headers, Stream body, CancellationToken cancellationToken, ResponseHandler responseHandler, Delegate next)
        {
            var context = new OwinContext(env, headers, body);
            var task = Run(context);
            if (task == null)
            {
                return InvokeNextApp(env, headers, body, cancellationToken, responseHandler, next);
            }
            return task
                .ContinueWith(
                    t =>
                    responseHandler(context.Response.Status.Code, context.Response.Headers ?? new Dictionary<string, string[]>(),
                                    context.Response.WriteFunction));
        }

        private static Task InvokeNextApp(IDictionary<string, object> env, IDictionary<string, string[]> headers, Stream body, CancellationToken cancellationToken,
                                          ResponseHandler responseHandler, Delegate next)
        {
            var nextApp = next as App;
            if (nextApp != null)
            {
                return nextApp(env, headers, body, cancellationToken, responseHandler, null);
            }
            return TaskHelper.Completed();
        }

        internal static Task Run(IContext context)
        {
            Startup();

            if (TryHandleAsStaticContent(context))
            {
                return MakeCompletedTask();
            }

            IDictionary<string, string[]> variables;
            var handlerType = TableFor(context.Request.HttpMethod).Get(context.Request.Url.AbsolutePath, context.Request.GetContentType(), context.Request.GetAccept(), out variables);
            if (handlerType == null) return null;
            var handlerInfo = new HandlerInfo(handlerType, variables, context.Request.HttpMethod);

            foreach (var key in context.Request.QueryString.Keys)
            {
                handlerInfo.Variables.Add(key, context.Request.QueryString[key]);
            }

            var task = PipelineFunctionFactory.Get(handlerInfo)(context);
            return task ?? MakeCompletedTask();
        }

        private static Task MakeCompletedTask()
        {
            var tcs = new TaskCompletionSource<object>();
            tcs.SetResult(null);
            return tcs.Task;
        }

        private static bool TryHandleAsStaticContent(IContext context)
        {
            var absolutePath = context.Request.Url.AbsolutePath;
            string file;
            if (SimpleWeb.Configuration.PublicFileMappings.ContainsKey(absolutePath))
            {
                file = SimpleWeb.Environment.PathUtility.MapPath(SimpleWeb.Configuration.PublicFileMappings[absolutePath]);
            }
            else if (
                SimpleWeb.Configuration.PublicFolders.Any(
                    folder => absolutePath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
            {
                file = SimpleWeb.Environment.PathUtility.MapPath(absolutePath);
            }
            else
            {
                return false;
            }

            if (!File.Exists(file)) return false;

            context.Response.Status = "200 OK";
            context.Response.SetContentType(GetContentType(file, context.Request.GetAccept()));
            context.Response.WriteFunction = (stream, token) =>
                {
                    using (var fileStream = File.OpenRead(file))
                    {
                        fileStream.CopyTo(stream);
                        return TaskHelper.Completed();
                    }
                };

            return true;
        }

        internal static string GetContentType(string file, IEnumerable<string> acceptTypes)
        {
            if (acceptTypes == null) return "text/plain";

			var types = acceptTypes.ToArray();

			if (types.All(r=>r == "*/*")) return GuessType(file);
            return types.FirstOrDefault() ?? "text/plain";
        }

    	static string GuessType(string file)
    	{
    		switch (file.ToLower().SubstringAfterLast('.'))
    		{
				case "js":
				case "javascript": return "text/javascript";

				case "css": return "text/css";

				case "jpg":
				case "jpeg": return "image/jpeg";
				case "png": return "image/png";
				case "gif": return "image/gif";

				default: return "text/plain";
    		}
    	}

    	private static void Startup()
        {
            if (_startupTaskRunner != null)
            {
                lock (StartupLock)
                {
                    if (_startupTaskRunner != null)
                    {
                        _startupTaskRunner.RunStartupTasks();
                        _startupTaskRunner = null;
                    }
                }
            }
        }

        private static readonly ConcurrentDictionary<string, RoutingTable> RoutingTables = new ConcurrentDictionary<string, RoutingTable>(StringComparer.OrdinalIgnoreCase);

        private static RoutingTable BuildRoutingTable(string httpMethod)
        {
            var debug = ExportedTypeHelper.FromCurrentAppDomain(t => true).ToList();
            var handlerTypes = ExportedTypeHelper.FromCurrentAppDomain(IsHttpMethodHandler)
                .Where(i => HttpMethodAttribute.Get(i).HttpMethod.Equals(httpMethod, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return new RoutingTableBuilder(handlerTypes).BuildRoutingTable();
        }

        private static bool IsHttpMethodHandler(Type type)
        {
            return (!type.IsInterface) && HttpMethodAttribute.IsAppliedTo(type);
        }

        private static RoutingTable TableFor(string httpMethod)
        {
            return RoutingTables.GetOrAdd(httpMethod, BuildRoutingTable);
        }
    }
}