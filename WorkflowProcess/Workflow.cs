using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using System.Net;
using System.Text;
using System.Text.Json;

namespace WorkflowProcess;
public class DurableFunOrchestrationHuman
{
    private readonly int delayInSeconds;
    private readonly ILogger<DurableFunOrchestrationHuman> _logger;

    public DurableFunOrchestrationHuman(
        ILogger<DurableFunOrchestrationHuman> logger)
    {
        delayInSeconds = 50;
        _logger = logger;
    }

    [Function(nameof(StartWorkflowOrchestrator))]
    public async Task<List<string>> StartWorkflowOrchestrator(
        [Microsoft.Azure.Functions.Worker.OrchestrationTrigger] TaskOrchestrationContext context)
    {

        var outputs = new List<string>();

        var entityId = new EntityInstanceId(nameof(WorkflowInstance), context.InstanceId.ToString());
        var workflowInitializer = new WorkflowInitializer(
            id: context.InstanceId.ToString(),
            workflowBlueprint: GetDummyWorkflowBlueprint()
        );

        await context.Entities.CallEntityAsync<WorkflowInstance>(entityId, "Initialize", workflowInitializer);
        //await context.Entities.CallEntityAsync<WorkflowInstance>(entityId, "GetWorkflowInstance");

        while (true)
        {
            List<ActivityInstance> activitiesToSchedule = await context.Entities.CallEntityAsync<List<ActivityInstance>>(entityId, "GetActivitiesToSchedule");
            bool AllActivitiesCompleted = await context.Entities.CallEntityAsync<bool>(entityId, "AllActivitiesCompleted");
            if (AllActivitiesCompleted)
            {
                outputs.Add(await context.CallActivityAsync<string>(nameof(LogActivity), "There is no activities to schedule."));
                return outputs;
            }
            else
            {
                var activitiesScheduled = new List<Task<int>>();
                foreach (var activity in activitiesToSchedule)
                {
                    Task<int> humanActivityTask = context.WaitForExternalEvent<int>(activity.Code);
                    activitiesScheduled.Add(humanActivityTask);
                    await context.Entities.CallEntityAsync<WorkflowInstance>(entityId, "ActivityScheduled", activity);
                    outputs.Add(await context.CallActivityAsync<string>(nameof(LogActivity), $"{activity.Name} has been scheduled."));
                }

                var delay = context.CurrentUtcDateTime.AddSeconds(delayInSeconds);
                await context.CreateTimer(delay, CancellationToken.None);
            }
        }
    }

    [Function(nameof(DurableHumanSayHello))]
    public string DurableHumanSayHello([Microsoft.Azure.Functions.Worker.ActivityTrigger] string name)
    {
        return $"Hello {name}!";
    }

    [Function(nameof(LogActivity))]
    public string LogActivity([Microsoft.Azure.Functions.Worker.ActivityTrigger] string message)
    {
        _logger.LogInformation(message);
        return message;
    }

    [Function(nameof(WorkflowStart))]
    [OpenApiOperation(operationId: "WorkflowStart", tags: new[] { "WorkflowStart" }, Summary = "Start a new workflow instance", Description = "This endpoint starts a new workflow instance.")]
    [OpenApiParameter(name: "dummy", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Dummy parameter for GET request")]
    [OpenApiResponseWithoutBody(statusCode: HttpStatusCode.OK, Description = "Returns the status of the workflow instance.")]

    public async Task<HttpResponseData> WorkflowStart(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req,
        [Microsoft.Azure.Functions.Worker.DurableClient] DurableTaskClient durableTaskClient)
    {
        string instanceId = await durableTaskClient.ScheduleNewOrchestrationInstanceAsync(nameof(StartWorkflowOrchestrator));
        string logMessage = $"Workflow Instance Instantiated with ID: {instanceId}";
        _logger.LogInformation(logMessage);

        return durableTaskClient.CreateCheckStatusResponse(req, instanceId);
    }

    [Function(nameof(GetWorkflowStatus))]
    [OpenApiOperation(operationId: "GetWorkflowStatus", tags: new[] { "Workflow" }, Summary = "Get the status of a workflow", Description = "This endpoint returns the status of a workflow instance.")]
    [OpenApiParameter(name: "entityKey", In = ParameterLocation.Path, Required = true, Type = typeof(string), Description = "The entity key of the workflow instance.")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(WorkflowInstance), Description = "The workflow instance status.")]
    public async Task<HttpResponseData> GetWorkflowStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "GetWorkflowStatus/{entityKey}")] HttpRequestData req,
        [Microsoft.Azure.Functions.Worker.DurableClient] DurableTaskClient client, string entityKey)
    {
        var entityId = new EntityInstanceId(nameof(WorkflowInstance), entityKey);
        var entity = await client.Entities.GetEntityAsync<WorkflowInstance>(entityId);

        var workflowInstance = JsonSerializer.Serialize(entity?.State);

        var content = new StringContent(workflowInstance, Encoding.UTF8, "application/json");

        HttpResponseData responseMessage = req.CreateResponse(System.Net.HttpStatusCode.OK);
        responseMessage.Body = content.ReadAsStream();

        return responseMessage;
    }

    [Function(nameof(UpdateActivity))]
    [OpenApiOperation(operationId: "UpdateActivity", tags: new[] { "Activity" }, Summary = "Update the activity status", Description = "This endpoint updates the activity status based on the request.")]
    [OpenApiRequestBody("application/json", typeof(UpdateActivityRequest), Description = "Request body containing activity update details", Required = true)]
    [OpenApiResponseWithoutBody(statusCode: HttpStatusCode.OK, Description = "Successfully updated the activity status")]

    public async Task UpdateActivity(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req,
        [Microsoft.Azure.Functions.Worker.DurableClient] DurableTaskClient durableTaskClient)
    {
        if (req.Body.CanRead)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                PropertyNameCaseInsensitive = true
            };
            var request = await JsonSerializer.DeserializeAsync<UpdateActivityRequest>(req.Body, options);
            if (request is not null)
            {
                var entityId = new EntityInstanceId("WorkflowInstance", request.InstanceId);

                string operationName = "ActivityPending";
                if (request.ApprovalStatus == 1) operationName = "CompleteActivity";
                else if (request.ApprovalStatus == 0) operationName = "ActivityFailed";

                await durableTaskClient.Entities.SignalEntityAsync(entityId, operationName, new ActivityInstance
                {
                    Code = request.ActivityCode,
                });

                //await durableTaskClient.RaiseEventAsync(request.InstanceId, request.ActivityCode, request.ApprovalStatus);
            }
        }
    }

    private static WorkflowBlueprint GetDummyWorkflowBlueprint()
    {
        return new()
        {
            Type = "Standard Workflow",
            Stages =
            [
                new()
                {
                  Name = "Simple Activity",
                  Activities =
                  [
                      new()
                      {
                          Name = "Simple One",
                          Code = "SimpleOne",
                            Type = "HUMAN"
                      },
                      new()
                      {
                          Name = "Simple Two",
                          Code = "SimpleTwo",
                            Type = "HUMAN"
                      }, new()
                      {
                          Name = "Simple Three",
                          Code = "SimpleThree",
                            Type = "HUMAN",
                            Dependencies = ["SimpleOne"]
                      },
                      new(){
                           Name = "Simple 4",
                          Code = "Simple4",
                            Type = "HUMAN",
                            Dependencies = ["SimpleTwo", "SimpleThree"]
                      },
                      new(){
                           Name = "Simple 5",
                          Code = "Simple5",
                            Type = "HUMAN",
                            Dependencies = ["SimpleOne", "SimpleTwo"]
                      }
                      ]
                },
                new()
                {
                    Name = "ASSIGN FO",
                    Activities =
                    [
                        new()
                        {
                            Name = "Assign FO User",
                            Code = "AssignFOUser",
                            Type = "HUMAN"
                        },
                        new ()
                        {
                            Name = "Accept Workflow",
                            Code = "AcceptWorkflow",
                            Type = "HUMAN",
                            Dependencies = ["AssignFOUser"]
                        }
                    ]
                },
                new(){
                    Name = "Assign User 2",
                    Activities =
                    [
                        new()
                        {
                            Name = "Assigne User",
                            Code = "ASSIGNEUSER",
                            Type = "HUMAN"
                        },
                        new()
                        {
                            Name = "AssigneRole",
                            Code = "ASSIGNEROLE",
                            Type = "HUMAN"
                        }
                    ]
                }
            ]
        };
    }
}
