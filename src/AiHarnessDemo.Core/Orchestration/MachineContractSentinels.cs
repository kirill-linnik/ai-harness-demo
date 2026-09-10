namespace AiHarnessDemo.Core.Orchestration;

internal static class MachineContractSentinels
{
    public static IReadOnlyList<int> FindStandalone(
        string output,
        string sentinel)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(sentinel);

        var matches = new List<int>();
        var searchIndex = 0;
        while (searchIndex <= output.Length - sentinel.Length)
        {
            var index = output.IndexOf(
                sentinel,
                searchIndex,
                StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }
            if (IsStandalone(output, index, sentinel))
            {
                matches.Add(index);
            }
            searchIndex = index + sentinel.Length;
        }
        return matches;
    }

    private static bool IsStandalone(
        string output,
        int index,
        string sentinel)
    {
        var beforeLine = index == 0 || output[index - 1] is '\r' or '\n';
        var afterIndex = index + sentinel.Length;
        return beforeLine &&
               (afterIndex == output.Length ||
                output[afterIndex] is '\r' or '\n');
    }
}
