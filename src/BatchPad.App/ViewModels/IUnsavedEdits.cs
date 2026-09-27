namespace BatchPad.App.ViewModels;

/// <summary>An editor or page whose edits are lost when it closes without saving.</summary>
public interface IUnsavedEdits
{
    bool HasUnsavedEdits { get; }

    /// <summary>What the edits are to, for "Discard your unsaved changes to …?".</summary>
    string EditsDescription { get; }
}
