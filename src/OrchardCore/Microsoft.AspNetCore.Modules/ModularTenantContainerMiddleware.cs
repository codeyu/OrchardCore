﻿using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchard.Environment.Shell;

namespace Microsoft.AspNetCore.Modules
{
    /// <summary>
    /// This middleware replaces the default service provider by the one for the current tenant
    /// </summary>
    public class ModularTenantContainerMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IShellHost _orchardHost;
        private readonly IRunningShellTable _runningShellTable;
        private readonly ILogger _logger;

        public ModularTenantContainerMiddleware(
            RequestDelegate next,
            IShellHost orchardHost,
            IRunningShellTable runningShellTable,
            ILogger<ModularTenantContainerMiddleware> logger)
        {
            _next = next;
            _orchardHost = orchardHost;
            _runningShellTable = runningShellTable;
            _logger = logger;
        }

        public Task Invoke(HttpContext httpContext)
        {
            // Ensure all ShellContext are loaded and available.
            _orchardHost.Initialize();

            var shellSetting = _runningShellTable.Match(httpContext);

            // We only serve the next request if the tenant has been resolved.
            if (shellSetting == null)
            {
                return Task.CompletedTask;
            }

            // Register the shell settings as a custom feature.
            httpContext.Features.Set(shellSetting);

            var shellContext = _orchardHost.GetOrCreateShellContext(shellSetting);

            using (var scope = shellContext.CreateServiceScope())
            {
                httpContext.RequestServices = scope.ServiceProvider;

                if (shellContext.IsActivated)
                {
                    return _next.Invoke(httpContext);
                }

                lock (shellContext)
                {
                    // The tenant gets activated here
                    if (!shellContext.IsActivated)
                    {
                        var tenantEvents = httpContext.RequestServices
                            .GetServices<IModularTenantEvents>();

                        Task.WaitAll(
                            tenantEvents.Select(te => te.ActivatingAsync()).ToArray());

                        httpContext.Items["BuildPipeline"] = true;
                        shellContext.IsActivated = true;
                                
                        Task.WaitAll(
                            tenantEvents.Select(te => te.ActivatedAsync()).ToArray());
                    }
                }
                    
                return _next.Invoke(httpContext);
            }
        }
    }
}