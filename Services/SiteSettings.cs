using System.Text.Json;
using Microsoft.JSInterop;
using TripleF.Models;

namespace TripleF.Services;

/// <summary>
/// Per-browser display preferences, kept in localStorage. WebAssembly can call localStorage
/// synchronously, so pages read these during render like any other property.
/// </summary>
public class SiteSettings
{
    private const string StorageKey = "triplef.settings";

    private readonly IJSInProcessRuntime js;

    public bool ShowInactiveManagers { get; private set; } = true;
    public bool ShowTradeValues { get; private set; } = true;

    public SiteSettings(IJSRuntime jsRuntime)
    {
        js = (IJSInProcessRuntime)jsRuntime;
        try
        {
            var json = js.Invoke<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrEmpty(json) && JsonSerializer.Deserialize<Stored>(json) is { } s)
            {
                ShowInactiveManagers = s.ShowInactiveManagers;
                ShowTradeValues = s.ShowTradeValues;
            }
        }
        catch
        {
            // Blocked or malformed storage: fall back to defaults.
        }
    }

    public void SetShowInactiveManagers(bool value) { ShowInactiveManagers = value; Save(); }
    public void SetShowTradeValues(bool value) { ShowTradeValues = value; Save(); }

    public void Reset()
    {
        ShowInactiveManagers = true;
        ShowTradeValues = true;
        Save();
    }

    /// <summary>Whether a manager should appear in leaderboards and pickers.</summary>
    public bool Shows(LeagueData d, string ownerId) => ShowInactiveManagers || d.ActiveOwnerIds.Contains(ownerId);

    private void Save()
    {
        try
        {
            js.InvokeVoid("localStorage.setItem", StorageKey,
                JsonSerializer.Serialize(new Stored { ShowInactiveManagers = ShowInactiveManagers, ShowTradeValues = ShowTradeValues }));
        }
        catch
        {
            // Private browsing or full storage: the setting still applies for this visit.
        }
    }

    private class Stored
    {
        public bool ShowInactiveManagers { get; set; } = true;
        public bool ShowTradeValues { get; set; } = true;
    }
}
