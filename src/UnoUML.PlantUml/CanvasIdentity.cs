using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UnoUML.PlantUml;

/// <summary>
/// Source identities for native families without explicit aliases. Length-prefixed
/// semantic components avoid delimiter ambiguity; physical line numbers are not identity.
/// </summary>
internal static class CanvasIdentity
{
    internal static string Create(string family, params string[] components)
    {
        var value = new StringBuilder();
        foreach (string component in components)
            value.Append(component.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(component);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString()));
        return family + ":" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
