using BANxOpen.Foundation.Contracts.Common;

namespace BAMaterial.Core.Common;

public readonly record struct BodyId(string Value) : IStronglyTypedId<string>
{
    public override string ToString() => Value;
}
