using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;
using SPT.Common.Http;

namespace ScavReroller.Client;

/// <summary>Reads one authoritative scav and leaves the PMC and its controllers intact.</summary>
internal sealed class ScavProfileRefresh
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo ScavProfileField = RequiredField("_savageProfile");
    private static readonly FieldInfo ModelField = RequiredField("_savageModelView");
    private static readonly FieldInfo PetIdField = typeof(Profile).GetField("PetId", Fields)
        ?? throw new MissingFieldException(typeof(Profile).FullName, "PetId");
    private readonly ClientBackendSession _session;
    private readonly MatchMakerSideSelectionScreen _screen;

    internal ScavProfileRefresh(ClientBackendSession session, MatchMakerSideSelectionScreen screen)
    {
        _session = session;
        _screen = screen;
        if (_session.ProfileOfPet == null || _session.AllProfiles == null)
            throw new InvalidOperationException("Player scav profile is not ready.");
        if (ModelField.GetValue(screen) == null)
            throw new InvalidOperationException("Character preview is not ready.");
    }

    internal async Task RefreshAsync(CancellationToken cancellation, Func<bool> isCurrent,
        Action<string> stage, Action profileApplied)
    {
        var originalPmc = _session.Profile;
        var originalScav = _session.ProfileOfPet;
        stage("profile-request");
        var snapshot = await OperationDeadline.Wait(_session.Send<ScavSnapshot>(new SendRequest
        {
            Url = RequestHandler.Host.TrimEnd('/') + "/scavreroller/profile",
            ParseInBackground = true,
            TimeoutSeconds = 15,
            Retries = SendRequest.NoRetries,
            HandlingMode = ERequestHandlingMode.AbortOnFail
        }), TimeSpan.FromSeconds(20), cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (snapshot == null || !snapshot.ok || snapshot.profile == null)
            throw new InvalidOperationException(snapshot?.message ?? "Empty scav snapshot. Install server mod 0.1.5.");
        if (snapshot.profile.Id.ToString() != snapshot.pmcScavId)
            throw new InvalidOperationException("Server returned an inconsistent scav identity.");

        stage("profile-decode");
        var newScav = new Profile(snapshot.profile);
        if (newScav.Inventory == null)
            throw new InvalidOperationException("New scav profile has no inventory.");
        if (newScav.Inventory.DeserializationErrors.Count != 0)
        {
            ItemFactory.LogErrors(newScav.Inventory.DeserializationErrors, "rerolled player scav");
            throw new InvalidOperationException("Scav inventory deserialization failed. See the game log.");
        }

        cancellation.ThrowIfCancellationRequested();
        if (!isCurrent() || !ReferenceEquals(Plugin.GetFikaSession(), _session) ||
            !ReferenceEquals(_session.Profile, originalPmc) ||
            !ReferenceEquals(_session.ProfileOfPet, originalScav) || Plugin.GameWorldPresent())
            throw new OperationCanceledException("Selection or game session changed while refreshing the scav.", cancellation);

        stage("profile-apply");
        var profiles = ProfileSlots.ReplaceSingleReference(_session.AllProfiles, originalScav, newScav);
        var previousProfiles = _session.AllProfiles;
        var previousPetId = originalPmc.PetId;
        // The server may have replaced the scav after a raid. Keep the same PMC
        // instance/controllers, but adopt its authoritative scav link.
        var linkChanged = previousPetId?.ToString() != snapshot.pmcScavId;
        try
        {
            if (linkChanged)
            {
                // PetId is an instance readonly field in this EFT build. Change
                // only the server-authoritative link, not the PMC object itself.
                MongoID? authoritativeId = snapshot.pmcScavId;
                PetIdField.SetValue(originalPmc, authoritativeId);
            }
            _session.AllProfiles = profiles;
            if (!ReferenceEquals(_session.ProfileOfPet, newScav))
                throw new InvalidOperationException("Session did not select the refreshed scav profile.");
        }
        catch
        {
            _session.AllProfiles = previousProfiles;
            if (linkChanged) PetIdField.SetValue(originalPmc, previousPetId);
            throw;
        }
        // Model errors must not mark an already synchronised inventory as stale.
        profileApplied();
        stage("screen-bind");
        ScavProfileField.SetValue(_screen, newScav);

        stage("preview-load");
        var model = (PlayerModelView)ModelField.GetValue(_screen);
        model.Close();
        try
        {
            await OperationDeadline.Wait(model.Show(newScav, null, () => { }, 5f, null, true),
                TimeSpan.FromSeconds(25), cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (isCurrent() && !model.LoadingComplete)
                throw new InvalidOperationException("Scav preview did not finish loading.");
        }
        catch
        {
            // Close cancels the native model loader. Do not close a later
            // screen's model if selection was closed/reopened in the meantime.
            if (isCurrent()) model.Close();
            throw;
        }
    }

    private static FieldInfo RequiredField(string name) =>
        typeof(MatchMakerSideSelectionScreen).GetField(name, Fields)
        ?? throw new MissingFieldException(typeof(MatchMakerSideSelectionScreen).FullName, name);

    internal sealed class ScavSnapshot
    {
        public bool ok = false;
        public string message = "";
        public ProfileDescriptor? profile = null;
        public string pmcScavId = "";
    }
}
