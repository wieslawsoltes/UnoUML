namespace UnoUML.PlantUml;

internal static class ModernStyle
{
    internal static bool TryExtract(string source, bool sequence, ManagedSkin skin, out string cleaned)
    {
        if (!StyleSheet.TryExtract(source, out cleaned, out var sheet)) return false;
        skin.Sheet = sheet;
        skin.Diagram = sequence ? "sequencediagram" : "classdiagram";
        skin.HasModernStyles = !sheet.IsEmpty;
        return true;
    }
}
