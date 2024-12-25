using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;

namespace WorkflowProcess;

public class UpdateActivityRequest
{
    public string InstanceId { get; set; }
    public string ActivityCode { get; set; }
    public int ApprovalStatus { get; set; }
}

public class WorkflowInitializer
{
    public WorkflowInitializer(string id, WorkflowBlueprint workflowBlueprint)
    {
        Id = id;
        WorkflowBlueprint = workflowBlueprint;
    }
    public string Id { get; set; }
    public WorkflowBlueprint WorkflowBlueprint { get; set; }
}

public class ActivityBluePrint
{
    public string Name { get; set; }
    public string Code { get; set; }
    public string Type { get; set; }
    public List<string> Dependencies { get; set; }
}
public class ActivityInstance
{
    public string Name { get; set; }
    public string Status { get; set; }
    public string Code { get; set; }
    public string Type { get; set; }
    public List<string> Dependencies { get; set; }
}
public class StageBluprint
{
    public string Name { get; set; }
    public List<ActivityBluePrint> Activities { get; set; }
}
public class StageInstance
{
    public string Name { get; set; }
    public string Status { get; set; }
    public List<ActivityInstance> ActivityInstance { get; set; }
}

public class WorkflowBlueprint
{
    public string Type { get; set; }
    public List<StageBluprint> Stages { get; set; }
}

[JsonObject(MemberSerialization = MemberSerialization.OptIn)]
public class WorkflowInstance(string id, string type, string currentStage, List<StageInstance> stages)
{
    [JsonProperty]
    public string Id { get; set; } = id;
    [JsonProperty]

    public string Type { get; set; } = type;
    [JsonProperty]

    public string CurrentStage { get; set; } = currentStage;
    [JsonProperty]
    public List<StageInstance> Stages { get; set; } = stages;

    public bool ActivitiesCompleted { get; set; } = false;

    public WorkflowInstance() : this("", "", "", []) { }

    public void Initialize(WorkflowInitializer workflowInitializer)
    {
        Id = workflowInitializer.Id;
        Type = workflowInitializer.WorkflowBlueprint.Type;
        Stages = workflowInitializer.WorkflowBlueprint.Stages.Select((stage, index) => new StageInstance
        {
            Name = stage.Name,
            Status = index == 0 ? "ACTIVE" : "PENDING",
            ActivityInstance = stage.Activities.Select(_ => new ActivityInstance
            {
                Code = _.Code,
                Name = _.Name,
                Status = "PENDING",
                Type = _.Type,
                Dependencies = _.Dependencies
            }).ToList()
        }).ToList();
        CurrentStage = workflowInitializer.WorkflowBlueprint.Stages.FirstOrDefault()!.Name;
    }

    public WorkflowInstance GetWorkflowInstance()
    {
        return this;
    }

    public bool AllActivitiesCompleted()
    {
        return this.ActivitiesCompleted;
    }

    public List<ActivityInstance> GetActivitiesToSchedule()
    {
        List<ActivityInstance> activitiesToSchedule = new List<ActivityInstance>();

        var currentStages = this.Stages.Find(s => s.Name == CurrentStage && s.Status == "ACTIVE");

        if (currentStages != null)
        {
            foreach (var activity in currentStages.ActivityInstance.Where(a => a.Status == "PENDING"))
            {
                bool dependenciesCompleted = true;
                if (activity.Dependencies != null)
                {
                    var completedActivitiesNames = currentStages.ActivityInstance
                        .Where(a => a.Status == "COMPLETED")
                        .Select(a => a.Code)
                        .ToList();

                    foreach (var dependency in activity.Dependencies)
                    {
                        if (!completedActivitiesNames.Exists(_ => _ == dependency))
                        {
                            dependenciesCompleted = false;
                            break;
                        }
                    }
                }

                if (dependenciesCompleted)
                {
                    activitiesToSchedule.Add(activity);
                }
            }
        }
        return activitiesToSchedule;
    }

    public void ActivityScheduled(ActivityInstance activityInstance)
    {
        UpdateActivityStatus("SCHEDULED", activityInstance);
    }

    public void ActivityFailed(ActivityInstance activityInstance)
    {
        UpdateActivityStatus("FAILED", activityInstance);
    }

    public void ActivityPending(ActivityInstance activityInstance)
    {
        UpdateActivityStatus("PENDING", activityInstance);
    }

    void UpdateActivityStatus(string status, ActivityInstance activityInstance)
    {
        Stages.ForEach(stage =>
        {
            stage.ActivityInstance
                .Where(activity => activity.Name == activityInstance.Name)
                .ToList()
                .ForEach(activity => activity.Status = status);
        });
    }

    public void CompleteActivity(ActivityInstance activity)
    {
        StageInstance stageContainingActivity = Stages.Find(stage => stage.Name == this.CurrentStage);
        if (stageContainingActivity != null)
        {
            ActivityInstance activityToUpdate = stageContainingActivity.ActivityInstance.Find(a => a.Code == activity.Code);
            if (activityToUpdate != null)
            {
                activityToUpdate.Status = "COMPLETED";

                bool allActivitiesCompleted = stageContainingActivity.ActivityInstance.TrueForAll(a => a.Status == "COMPLETED");

                if (allActivitiesCompleted)
                {
                    stageContainingActivity.Status = "COMPLETED";
                    ActivateNextStage();
                }
            }
        }
    }

    void ActivateNextStage()
    {
        StageInstance nextPendingStage = Stages.Find(s => s.Status == "PENDING");

        if (nextPendingStage != null)
        {
            this.CurrentStage = nextPendingStage.Name;
            nextPendingStage.Status = "ACTIVE";
            ActivityInstance nextPendingActivity = nextPendingStage.ActivityInstance.Find(a => a.Status == "PENDING");
            if (nextPendingActivity != null)
            {
                nextPendingActivity.Status = "PENDING";
            }
        }
        else
        {
            this.ActivitiesCompleted = true;
        }
    }

    [Function(nameof(WorkflowInstance))]
    public static Task Run([EntityTrigger] TaskEntityDispatcher dispatcher) => dispatcher.DispatchAsync<WorkflowInstance>();
}