namespace Optimum.Render.Vulkan.Graph;

// LOCAL STUB (stage "frame-plan"): contract C1 is owned by stage "barriers", which also
// defines UsageState.For next to this enum. This file exists only so FramePlan compiles
// before the merge; the integration stage keeps the barriers stage's definition and
// drops this file. The member list is copied verbatim from the contract.

/// <summary>How a pass uses one resource. Stage and access derive from this, never from the layout alone.</summary>
public enum ResourceUsage
{
    ColorWrite,
    ColorBlend,
    DepthWrite,
    DepthReadOnly,
    DepthReadOnlySampled,
    SampleFragment,
    SampleVertex,
    StorageRead,
    TransferSrc,
    TransferDst,
    PresentSrc,
}
