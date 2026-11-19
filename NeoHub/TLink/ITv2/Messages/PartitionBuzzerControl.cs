using DSC.TLink.ITv2.Enumerations;

namespace DSC.TLink.ITv2.Messages
{
    /// <summary>
    /// Sounds a partition's keypad buzzer (ModuleControl 0x0914).
    /// <para>
    /// <b>Experimental.</b> The payload layout isn't documented anywhere public, so for now the
    /// body after the command sequence is sent exactly as <see cref="Payload"/>, for working out
    /// the format from the Dev Console. The panel rejects a malformed payload with a Nack that
    /// names the problem (InvalidCommandLength, InvalidPartition, AccessCodeRequired, ...).
    /// Once the format is known, this becomes typed fields.
    /// </para>
    /// </summary>
    [ITv2Command(ITv2Command.ModuleControl_Partition_Buzzer_Control)]
    public record PartitionBuzzerControl : CommandMessageBase
    {
        /// <summary>The raw body, sent as-is (e.g. <c>01 01</c> = partition 1 as a compact integer).</summary>
        public byte[] Payload { get; init; } = [];
    }
}
