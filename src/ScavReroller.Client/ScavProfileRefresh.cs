using System;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;
using SPT.Common.Http;
using UnityEngine;

namespace ScavReroller.Client;

/// <summary>Updates the scav slot without replacing the live PMC or its controllers.</summary>
internal sealed class ScavProfileRefresh : IDisposable
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo ScavProfileField = RequiredField("_savageProfile");
    private static readonly FieldInfo ModelField = RequiredField("_savageModelView");
    private readonly ClientBackendSession _session;
    private readonly MatchMakerSideSelectionScreen _screen;
    private readonly DefaultUIButton _next;
    private readonly DefaultUIButton _back;
    private readonly bool _nextWasEnabled;
    private readonly bool _backWasEnabled;

    internal ScavProfileRefresh(ClientBackendSession session, MatchMakerSideSelectionScreen screen)
    {
        _session = session;
        _screen = screen;
        _next = (DefaultUIButton)RequiredField("_nextButton").GetValue(screen);
        _back = (DefaultUIButton)RequiredField("_backButton").GetValue(screen);
        if (_next == null || _back == null || ModelField.GetValue(screen) == null)
            throw new InvalidOperationException("Character selection UI is not ready.");
        _nextWasEnabled = _next.Interactable;
        _backWasEnabled = _back.Interactable;
        if (_session.ProfileOfPet == null || _session.AllProfiles == null)
            throw new InvalidOperationException("Player scav profile is not ready.");
        KeepNavigationBlocked();
    }

    internal void KeepNavigationBlocked()
    {
        if (_next != null) _next.Interactable = false;
        if (_back != null) _back.Interactable = false;
    }

    internal bool Matches(ClientBackendSession session, MatchMakerSideSelectionScreen screen) =>
        ReferenceEquals(_session, session) && ReferenceEquals(_screen, screen);

    internal async Task RefreshAsync()
    {
        var originalPmc = _session.Profile;
        var originalScav = _session.ProfileOfPet;
        var scavId = originalScav.Id;

        // Same request and deserializer as EftClientBackendSession.GetProfiles,
        // without its replacement of the PMC and InsuranceCompany.
        var descriptors = await _session.Send<ProfileDescriptor[]>(new SendRequest
        {
            Url = RequestHandler.Host.TrimEnd('/') + "/client/game/profile/list",
            ParseInBackground = true,
            Retries = SendRequest.DefaultRetries,
            HandlingMode = ERequestHandlingMode.AbortOnFail
        });

        ProfileDescriptor? scavDescriptor = null;
        foreach (var descriptor in descriptors)
        {
            if (descriptor.Id.ToString() != scavId) continue;
            if (scavDescriptor != null)
                throw new InvalidOperationException("Server returned duplicate scav profiles.");
            scavDescriptor = descriptor;
        }
        if (scavDescriptor == null)
            throw new InvalidOperationException("Server response did not contain the current player scav.");

        var newScav = new Profile(scavDescriptor);
        if (newScav.Inventory == null)
            throw new InvalidOperationException("New scav profile has no inventory.");
        if (newScav.Inventory.DeserializationErrors.Count != 0)
        {
            ItemFactory.LogErrors(newScav.Inventory.DeserializationErrors, "rerolled player scav");
            throw new InvalidOperationException("New scav inventory could not be loaded. See the game log.");
        }

        if (!ReferenceEquals(Plugin.GetFikaSession(), _session) ||
            !ReferenceEquals(_session.Profile, originalPmc) ||
            !ReferenceEquals(_session.ProfileOfPet, originalScav) || Plugin.GameWorldPresent())
            throw new InvalidOperationException("Game session changed while refreshing the scav.");

        var profiles = ProfileSlots.ReplaceSingleReference(_session.AllProfiles, originalScav, newScav);
        var previousProfiles = _session.AllProfiles;
        _session.AllProfiles = profiles;
        if (!ReferenceEquals(_session.ProfileOfPet, newScav))
        {
            _session.AllProfiles = previousProfiles;
            throw new InvalidOperationException("Session did not select the refreshed scav profile.");
        }

        // If the screen was closed by the game, its next Show will read the
        // updated session. Never reopen a closed screen from an async callback.
        if (_screen == null || !_screen.gameObject.activeInHierarchy) return;
        ScavProfileField.SetValue(_screen, newScav);
        var model = (PlayerModelView)ModelField.GetValue(_screen);
        model.Close();
        // The stock scav Show uses no inventory controller, a no-op callback,
        // a five-second delay, no custom position and the final flag enabled.
        await model.Show(newScav, null, () => { }, 5f, null, true);
        if (_screen != null && _screen.gameObject.activeInHierarchy && !model.LoadingComplete)
            throw new InvalidOperationException("Scav preview did not finish loading.");
    }

    public void Dispose()
    {
        if (_next != null) _next.Interactable = _nextWasEnabled;
        if (_back != null) _back.Interactable = _backWasEnabled;
    }

    private static FieldInfo RequiredField(string name) =>
        typeof(MatchMakerSideSelectionScreen).GetField(name, Fields)
        ?? throw new MissingFieldException(typeof(MatchMakerSideSelectionScreen).FullName, name);
}
