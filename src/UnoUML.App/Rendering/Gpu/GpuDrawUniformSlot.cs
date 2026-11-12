namespace UnoUML.App.Rendering.Gpu;

/// <summary>
/// The uniform contents last used at one painter-order slot. Compare at the same
/// draw index: a reorder changes the occupant, while an appended overlay leaves
/// the diagram prefix untouched. Command records are immutable scene snapshots.
/// </summary>
internal readonly record struct GpuDrawUniformSlot(GpuCommand? Command, uint StopOffset)
{
    internal bool NeedsUpload(GpuCommand command, uint stopOffset, bool arenaReplaced) =>
        arenaReplaced || !ReferenceEquals(Command, command) || StopOffset != stopOffset;
}
