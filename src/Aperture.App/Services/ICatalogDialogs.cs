namespace Aperture.App.Services;

/// <summary>
/// The dialogs the library switcher needs. The app shows real WPF/Windows dialogs (<see cref="WpfCatalogDialogs"/>);
/// tests script the answers, so the switcher's logic is tested without a window.
/// </summary>
public interface ICatalogDialogs
{
    /// <summary>Asks for a line of text. Returns <c>null</c> if the user cancels.</summary>
    string? PromptText(string title, string label, string initial);

    /// <summary>Asks the user to choose a folder, starting in <paramref name="initialDirectory"/>. Returns <c>null</c> if cancelled.</summary>
    string? PickFolder(string title, string initialDirectory);

    /// <summary>Asks the user to confirm a destructive step (OK / Cancel). Returns <c>true</c> only for OK.</summary>
    bool Confirm(string title, string message);

    /// <summary>Tells the user something; <paramref name="isError"/> picks the error icon.</summary>
    void ShowMessage(string title, string message, bool isError);
}
