namespace SvetaRecipes.App.Services;

/// <summary>
/// A part of the app she pointed at for the assistant (<c>Views/PickOverlay</c>): the window with the element outlined,
/// a close-up of it, a short name for the chip ("TextBox \"Servings\"") and the description sent to Claude.
/// </summary>
public sealed record PickedArea(string ImagePath, string CloseUpPath, string Summary, string Description);
