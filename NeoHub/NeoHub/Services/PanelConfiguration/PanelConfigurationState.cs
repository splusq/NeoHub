using NeoHub.Services.PanelConfiguration.Sections;

namespace NeoHub.Services.PanelConfiguration;

/// <summary>
/// Holds all installer configuration data read from the panel via SectionRead.
/// One instance per session, stored on SessionState.Configuration.
/// Null on SessionState until a config read is explicitly triggered by the user.
/// </summary>
public class PanelConfigurationState
{
    public PanelCapabilities Capabilities { get; }

    public ZoneDefinitionSection ZoneDefinitions { get; }
    public ZoneAttributeSection ZoneAttributes { get; }
    public ZoneEventReportingSection ZoneEventReporting { get; }
    public ZoneLabelSection ZoneLabels { get; }
    public ZoneAssignmentSection ZoneAssignments { get; }
    public PartitionLabelSection PartitionLabels { get; }
    public PartitionEnableSection PartitionEnables { get; }

    /// <summary>All sections in read order, for generic iteration (read, export, import).</summary>
    public IReadOnlyList<IConfigSection> AllSections { get; }

    public DateTime? LastReadAt { get; set; }

    public PanelConfigurationState(PanelCapabilities capabilities)
    {
        Capabilities = capabilities;
        ZoneDefinitions = new(capabilities);
        ZoneAttributes = new(capabilities);
        ZoneEventReporting = new(capabilities);
        ZoneLabels = new(capabilities);
        ZoneAssignments = new(capabilities);
        PartitionLabels = new(capabilities);
        PartitionEnables = new(capabilities);

        AllSections =
        [
            ZoneDefinitions,
            ZoneAttributes,
            ZoneEventReporting,
            PartitionEnables,
            ZoneAssignments,
            ZoneLabels,
            PartitionLabels,
        ];
    }

    /// <summary>
    /// A deep copy (each section exported and imported). Installer configuration held by the
    /// panel state store is never modified once published: code that wants to change it edits a
    /// clone and publishes the clone with <see cref="Domain.ConfigurationReplaced"/>.
    /// </summary>
    public PanelConfigurationState Clone()
    {
        var copy = new PanelConfigurationState(Capabilities) { LastReadAt = LastReadAt };
        for (int i = 0; i < AllSections.Count; i++)
        {
            var data = AllSections[i].Export();
            if (data.Length > 0)   // an unread section stays unread in the copy
                copy.AllSections[i].Import(data);
        }
        return copy;
    }
}
