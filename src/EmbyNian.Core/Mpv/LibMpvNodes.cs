using System.Runtime.InteropServices;
using EmbyNian.Diagnostics;

namespace EmbyNian.Mpv;

/// <summary>
/// Reads mpv's <c>mpv_node</c> trees — the format every list-shaped property comes back in
/// (<c>track-list</c>, <c>audio-device-list</c>).
/// <para>
/// One reader rather than one per caller. These five functions are all pointer arithmetic against a C union,
/// and each of them encodes something that is easy to get wrong and impossible to see afterwards: an int64
/// lives <b>in</b> the union rather than behind it, a flag is a 4-byte int whose upper half is stale heap
/// bytes, and a map's keys and values are two parallel arrays. A second copy of that would not stay in step.
/// </para>
/// <para>
/// The caller owns the buffer and must free the tree — <see cref="Read"/> shows the whole pattern.
/// </para>
/// </summary>
internal static class LibMpvNodes
{
    private const string Category = "mpv";

    /// <summary>
    /// Reads one node-valued property and hands the tree to <paramref name="parse"/>, then frees it whatever
    /// happens. Returns <paramref name="fallback"/> when mpv has no answer.
    /// <para>
    /// The allocate/read/parse/free dance is here rather than at each call site because the free is the half
    /// that gets forgotten: mpv fills in a tree it allocated itself, and nothing else will ever release it.
    /// </para>
    /// </summary>
    internal static T Read<T>(IntPtr context, string property, Func<LibMpvNative.MpvNode, T> parse, T fallback)
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpvNative.MpvNode>());
        try
        {
            Marshal.StructureToPtr(new LibMpvNative.MpvNode(), buffer, fDeleteOld: false);

            if (LibMpvNative.mpv_get_property(context, property, LibMpvNative.FormatNode, buffer) < 0)
                return fallback;

            return parse(Marshal.PtrToStructure<LibMpvNative.MpvNode>(buffer));
        }
        finally
        {
            LibMpvNative.mpv_free_node_contents(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static IEnumerable<LibMpvNative.MpvNode> Children(LibMpvNative.MpvNode node)
    {
        if (node.Format is not (LibMpvNative.FormatNodeArray or LibMpvNative.FormatNodeMap)) return [];

        var list = Marshal.PtrToStructure<LibMpvNative.MpvNodeList>(node.Union);
        var values = new LibMpvNative.MpvNode[Math.Max(0, list.Num)];
        for (var index = 0; index < values.Length; index++)
            values[index] = Marshal.PtrToStructure<LibMpvNative.MpvNode>(list.Values + index * Marshal.SizeOf<LibMpvNative.MpvNode>());
        return values;
    }

    internal static IReadOnlyDictionary<string, LibMpvNative.MpvNode> Map(LibMpvNative.MpvNode node)
    {
        if (node.Format != LibMpvNative.FormatNodeMap) return new Dictionary<string, LibMpvNative.MpvNode>();

        var list = Marshal.PtrToStructure<LibMpvNative.MpvNodeList>(node.Union);
        var map = new Dictionary<string, LibMpvNative.MpvNode>(Math.Max(0, list.Num), StringComparer.Ordinal);
        for (var index = 0; index < list.Num; index++)
        {
            var key = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(list.Keys, index * IntPtr.Size));
            if (key is null) continue;
            var value = Marshal.PtrToStructure<LibMpvNative.MpvNode>(list.Values + index * Marshal.SizeOf<LibMpvNative.MpvNode>());
            if (value.Format is < 1 or > 8)
            {
                Log.Warn(Category, $"节点字段异常：key={key} 格式={value.Format} 索引={index}/{list.Num}");
                continue;
            }
            map[key] = value;
        }

        return map;
    }

    internal static string? String(IReadOnlyDictionary<string, LibMpvNative.MpvNode> map, string key) =>
        map.TryGetValue(key, out var node) && node.Format == LibMpvNative.FormatString
            ? Marshal.PtrToStringUTF8(node.Union)
            : null;

    internal static int? Int(IReadOnlyDictionary<string, LibMpvNative.MpvNode> map, string key)
    {
        if (!map.TryGetValue(key, out var node)) return null;

        // The union holds the value itself for FORMAT_INT64/FORMAT_DOUBLE — not a pointer —
        // so it is read as raw bits rather than dereferenced.
        return node.Format switch
        {
            LibMpvNative.FormatInt64 => unchecked((int)node.Union.ToInt64()),
            LibMpvNative.FormatDouble => (int)Math.Round(BitConverter.Int64BitsToDouble(node.Union.ToInt64())),
            _ => null
        };
    }

    internal static bool Flag(IReadOnlyDictionary<string, LibMpvNative.MpvNode> map, string key)
    {
        // mpv's C union declares the flag as a 4-byte int; the other half of the union is stale
        // heap bytes, so only the low word may be looked at — ToInt32 would reject the garbage.
        return map.TryGetValue(key, out var node) && node.Format == LibMpvNative.FormatFlag && (node.Union.ToInt64() & 1) != 0;
    }
}
