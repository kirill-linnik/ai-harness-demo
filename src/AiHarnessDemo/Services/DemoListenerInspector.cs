using System.Globalization;
using System.Runtime.InteropServices;

namespace AiHarnessDemo.Services;

internal static class DemoListenerInspector
{
    private const uint ErrorInsufficientBuffer = 122;
    private const int AddressFamilyInterNetwork = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint SnapshotProcesses = 0x00000002;

    public static DemoListenerOwnershipResult Verify(
        int rootProcessId,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? VerifyWindows(rootProcessId, port, cancellationToken)
                : OperatingSystem.IsLinux()
                    ? VerifyLinux(rootProcessId, port, cancellationToken)
                    : Unverifiable(
                        "Listener ownership verification is not supported on this operating system.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Unverifiable(
                $"Listener ownership inspection failed: {exception.Message}");
        }
    }

    internal static DemoListenerOwnershipResult Evaluate(
        int rootProcessId,
        IReadOnlyCollection<ListenerOwner> listeners,
        IReadOnlyDictionary<int, int> parents)
    {
        if (listeners.Any(item => item.Wildcard))
        {
            return NotOwned(
                "A wildcard or non-IPv4-loopback listener is bound to the assigned port.");
        }

        var exact = listeners
            .Where(item => item.Loopback)
            .ToList();
        if (exact.Count == 0)
        {
            return NotOwned(
                "No listener is bound to the assigned 127.0.0.1 port.");
        }

        foreach (var listener in exact)
        {
            var relationship = IsRootOrDescendant(
                listener.ProcessId,
                rootProcessId,
                parents);
            if (relationship is null)
            {
                return Unverifiable(
                    $"The process ancestry for listener PID {listener.ProcessId} could not be verified.");
            }
            if (!relationship.Value)
            {
                return NotOwned(
                    $"Listener PID {listener.ProcessId} is outside the recorded demo process tree.");
            }
        }

        return new DemoListenerOwnershipResult(
            DemoListenerOwnership.Owned,
            "The exact loopback listener belongs to the recorded demo process tree.");
    }

    private static DemoListenerOwnershipResult VerifyWindows(
        int rootProcessId,
        int port,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var listeners = ReadWindowsListeners(
            AddressFamilyInterNetwork,
            port,
            cancellationToken);
        var parents = ReadWindowsParents(cancellationToken);
        return Evaluate(rootProcessId, listeners, parents);
    }

    private static List<ListenerOwner> ReadWindowsListeners(
        int addressFamily,
        int port,
        CancellationToken cancellationToken)
    {
        var size = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            false,
            addressFamily,
            TcpTableOwnerPidListener,
            0);
        if (result != ErrorInsufficientBuffer)
        {
            throw new InvalidOperationException(
                $"Windows TCP listener inspection returned error {result}.");
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref size,
                false,
                addressFamily,
                TcpTableOwnerPidListener,
                0);
            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"Windows TCP listener inspection returned error {result}.");
            }

            var count = Marshal.ReadInt32(buffer);
            const int rowSize = 24;
            const int localPortOffset = 8;
            const int processIdOffset = 20;
            var listeners = new List<ListenerOwner>();
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = IntPtr.Add(buffer, sizeof(uint) + (index * rowSize));
                var candidatePort = NetworkPort(
                    unchecked((uint)Marshal.ReadInt32(
                        row,
                        localPortOffset)));
                if (candidatePort != port)
                {
                    continue;
                }

                var processId = Marshal.ReadInt32(row, processIdOffset);
                var address = unchecked((uint)Marshal.ReadInt32(row, 4));
                var bytes = BitConverter.GetBytes(address);
                var loopback =
                    bytes is [127, 0, 0, 1];
                var wildcard = bytes is [0, 0, 0, 0];
                if (!loopback && !wildcard)
                {
                    continue;
                }
                listeners.Add(new ListenerOwner(
                    processId,
                    loopback,
                    wildcard));
            }
            return listeners;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Dictionary<int, int> ReadWindowsParents(
        CancellationToken cancellationToken)
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == new IntPtr(-1))
        {
            throw new InvalidOperationException(
                $"Windows process snapshot failed with error {Marshal.GetLastWin32Error()}.");
        }
        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>()
            };
            var parents = new Dictionary<int, int>();
            if (!Process32First(snapshot, ref entry))
            {
                throw new InvalidOperationException(
                    $"Windows process enumeration failed with error {Marshal.GetLastWin32Error()}.");
            }
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                parents[unchecked((int)entry.ProcessId)] =
                    unchecked((int)entry.ParentProcessId);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
            return parents;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static DemoListenerOwnershipResult VerifyLinux(
        int rootProcessId,
        int port,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists("/proc"))
        {
            return Unverifiable("Linux procfs is unavailable.");
        }

        var sockets = ReadLinuxSockets("/proc/net/tcp", port);
        if (sockets.Count == 0)
        {
            return NotOwned(
                "No listener is bound to the assigned 127.0.0.1 port.");
        }

        var owners = FindLinuxSocketOwners(
            sockets.Select(item => item.Inode).ToHashSet(),
            cancellationToken);
        if (sockets.Any(item => !owners.ContainsKey(item.Inode)))
        {
            return Unverifiable(
                "At least one listener socket could not be attributed to a process.");
        }

        var parents = ReadLinuxParents(cancellationToken);
        var listeners = sockets
            .SelectMany(item => owners[item.Inode].Select(processId =>
                new ListenerOwner(
                    processId,
                    item.Loopback,
                    item.Wildcard)))
            .ToList();
        return Evaluate(rootProcessId, listeners, parents);
    }

    private static List<LinuxSocket> ReadLinuxSockets(
        string path,
        int port)
    {
        var sockets = new List<LinuxSocket>();
        if (!File.Exists(path))
        {
            return sockets;
        }
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var fields = line.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10 ||
                !string.Equals(fields[3], "0A", StringComparison.Ordinal))
            {
                continue;
            }
            var endpoint = fields[1].Split(':', 2);
            if (endpoint.Length != 2 ||
                !int.TryParse(
                    endpoint[1],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var candidatePort) ||
                candidatePort != port ||
                !long.TryParse(
                    fields[9],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var inode))
            {
                continue;
            }

            var loopback =
                string.Equals(endpoint[0], "0100007F", StringComparison.Ordinal);
            var wildcard =
                string.Equals(endpoint[0], "00000000", StringComparison.Ordinal);
            if (!loopback && !wildcard)
            {
                continue;
            }
            sockets.Add(new LinuxSocket(
                inode,
                loopback,
                wildcard));
        }
        return sockets;
    }

    private static Dictionary<long, HashSet<int>> FindLinuxSocketOwners(
        IReadOnlySet<long> socketInodes,
        CancellationToken cancellationToken)
    {
        var owners = new Dictionary<long, HashSet<int>>();
        foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!int.TryParse(
                    Path.GetFileName(processDirectory),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var processId))
            {
                continue;
            }
            var descriptors = Path.Combine(processDirectory, "fd");
            try
            {
                foreach (var descriptor in Directory.EnumerateFiles(descriptors))
                {
                    var target = new FileInfo(descriptor).LinkTarget;
                    if (target is null ||
                        !target.StartsWith("socket:[", StringComparison.Ordinal) ||
                        !target.EndsWith(']') ||
                        !long.TryParse(
                            target.AsSpan(8, target.Length - 9),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var inode) ||
                        !socketInodes.Contains(inode))
                    {
                        continue;
                    }
                    if (!owners.TryGetValue(inode, out var processIds))
                    {
                        processIds = [];
                        owners[inode] = processIds;
                    }
                    processIds.Add(processId);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A listener inode with no readable owner is handled as unverifiable.
            }
        }
        return owners;
    }

    private static Dictionary<int, int> ReadLinuxParents(
        CancellationToken cancellationToken)
    {
        var parents = new Dictionary<int, int>();
        foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!int.TryParse(
                    Path.GetFileName(processDirectory),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var processId))
            {
                continue;
            }
            try
            {
                var stat = File.ReadAllText(Path.Combine(processDirectory, "stat"));
                var closingParenthesis = stat.LastIndexOf(')');
                if (closingParenthesis < 0)
                {
                    continue;
                }
                var fields = stat[(closingParenthesis + 1)..].Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2 &&
                    int.TryParse(
                        fields[1],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parentProcessId))
                {
                    parents[processId] = parentProcessId;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Missing ancestry is handled as unverifiable.
            }
        }
        return parents;
    }

    private static bool? IsRootOrDescendant(
        int processId,
        int rootProcessId,
        IReadOnlyDictionary<int, int> parents)
    {
        var visited = new HashSet<int>();
        var current = processId;
        while (current > 0)
        {
            if (current == rootProcessId)
            {
                return true;
            }
            if (!visited.Add(current))
            {
                return null;
            }
            if (!parents.TryGetValue(current, out var parent))
            {
                return null;
            }
            current = parent;
        }
        return false;
    }

    private static int NetworkPort(uint value) =>
        unchecked((ushort)System.Net.IPAddress.NetworkToHostOrder(
            unchecked((short)(value & 0xffff))));

    private static DemoListenerOwnershipResult NotOwned(string detail) =>
        new(DemoListenerOwnership.NotOwned, detail);

    private static DemoListenerOwnershipResult Unverifiable(string detail) =>
        new(DemoListenerOwnership.Unverifiable, detail);

    internal sealed record ListenerOwner(
        int ProcessId,
        bool Loopback,
        bool Wildcard);

    private sealed record LinuxSocket(
        long Inode,
        bool Loopback,
        bool Wildcard);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(
        uint flags,
        uint processId);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(
        IntPtr snapshot,
        ref ProcessEntry32 entry);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(
        IntPtr snapshot,
        ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
