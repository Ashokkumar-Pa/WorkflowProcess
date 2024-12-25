using Microsoft.Azure.Functions.Worker.Extensions.OpenApi.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(worker => worker.UseNewtonsoftJson())
    .ConfigureOpenApi()
    .ConfigureServices(services =>
    {
        services.AddSingleton(provider =>
        {
            var options = new OpenApiInfo
            {
                Version = "v1",
                Title = "Azure Durable Functions API",
                Description = "An API to demonstrate Swagger integration with Azure Durable Functions"
            };
            return options;
        });
    })
    .Build();

host.Run();
