using BAMaterial.Core.Common;
using NxOpen.Foundation.Core.RuleEngine;

namespace BAMaterial.Core.Assignment;

public interface IAssignmentPlanFinalizer : IPlanFinalizer<AssignmentPlan, MaterialAssignmentPlanningInput, BodyId, ExecutablePlan>
{
}
