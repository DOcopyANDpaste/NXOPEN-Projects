using Core.Common;
using NxOpen.Foundation.Core.RuleEngine;

namespace Core.Assignment;

public interface IAssignmentPlanFinalizer : IPlanFinalizer<AssignmentPlan, MaterialAssignmentPlanningInput, BodyId, ExecutablePlan>
{
}
