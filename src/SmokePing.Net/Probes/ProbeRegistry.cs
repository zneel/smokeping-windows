namespace SmokePing.Net.Probes;

/// <summary>Resolves probe names from the configuration to probe implementations.</summary>
public sealed class ProbeRegistry
{
    private readonly Dictionary<string, IProbe> _probes;

    public ProbeRegistry(IEnumerable<IProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = probes.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> Names => _probes.Keys;

    public bool Contains(string name) => _probes.ContainsKey(name);

    /// <exception cref="KeyNotFoundException">No probe is registered under that name.</exception>
    public IProbe Get(string name) => _probes.TryGetValue(name, out var probe)
        ? probe
        : throw new KeyNotFoundException($"Unknown probe '{name}'. Available: {string.Join(", ", _probes.Keys)}.");
}
