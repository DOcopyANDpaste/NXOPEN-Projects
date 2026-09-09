using NxOpen.Foundation.Contracts.Common;

namespace Core.Common;

public readonly record struct BodyId(string Value) : IStronglyTypedId<string>
{
    public override string ToString() => Value;
}
