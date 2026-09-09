using BAMaterial.Core.Common;
using BANxOpen.Foundation.Core.RuleEngine;

namespace BAMaterial.Core.Assignment;

public interface IAssignmentPlanFinalizer : IPlanFinalizer<AssignmentPlan, MaterialAssignmentPlanningInput, BodyId, ExecutablePlan>
{
}
