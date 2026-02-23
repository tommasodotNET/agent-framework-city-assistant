namespace SharedServices;

/// <summary>
/// Specifies the storage policy to apply when chat history reduction occurs.
/// </summary>
public enum ReductionStoragePolicy
{
    /// <summary>
    /// Clears the existing messages and replaces them with the reduced set.
    /// This is the most storage-efficient option but loses the original history.
    /// </summary>
    Clear,

    /// <summary>
    /// Archives the existing messages by renaming their conversationId with an "_archived_{timestamp}" suffix,
    /// then stores the reduced messages with the original conversationId.
    /// This preserves the original history for audit/recovery purposes.
    /// </summary>
    Archive
}
