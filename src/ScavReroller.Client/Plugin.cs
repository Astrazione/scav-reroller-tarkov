using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using EFT.UI.Matchmaker;
using HarmonyLib;
using SPT.Common.Http;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ScavReroller.Client;

/// <summary>
/// Conservative MVP: an IMGUI button shown only on the character selection screen.
/// Refreshes the session's scav slot and preview after server-side generation.
/// </summary>
[BepInPlugin("local.scav.loadout.reroller.client", "Scav Loadout Reroller", "0.1.11")]
[BepInDependency("com.SPT.core")]
public sealed class Plugin : BaseUnityPlugin
{
    private ConfigEntry<bool> _showButton = null!;
    private ConfigEntry<float> _buttonCenterX = null!;
    private ConfigEntry<float> _buttonY = null!;
    private readonly RerollRecoveryState _recovery = new RerollRecoveryState();
    private SelectionNavigationGuard? _navigation;
    private CancellationTokenSource? _operationCancellation;
    private ClientBackendSession? _recoverySession;
    private int _screenVersion;
    private Harmony? _harmony;
    private static Plugin? _instance;
    private string _status = "";
    private GUIStyle? _buttonStyle;
    private GUIStyle? _labelStyle;
    private Texture2D? _eyeTexture;
    private GUIContent? _eyeContent;
    private ScavInventoryPreview? _inventoryPreview;
    private MatchMakerSideSelectionScreen? _sideSelectionScreen;
    private MenuOverlay? _overlay;
    private static MethodInfo? _getFikaSession;

    private void Awake()
    {
        _instance = this;
        _showButton = Config.Bind("UI", "ShowButton", true,
            "Show Scav Reroll and inventory preview buttons on the character selection screen.");
        _buttonCenterX = Config.Bind("UI", "ButtonCenterX", 0.382f,
            "Relative X position (0..1). Default aligns with SCAV on the provided 16:9 selection screen.");
        _buttonY = Config.Bind("UI", "ButtonY", 0.66f,
            "Relative Y position (0..1). Default is below SCAV select button.");
        // Only this component receives frame/GUI callbacks, and only while the
        // character selection screen is open. The plugin itself stays enabled
        // so configuration managers can continue to discover its settings.
        _overlay = gameObject.AddComponent<MenuOverlay>();
        _overlay.Owner = this;
        _overlay.enabled = false;
        _harmony = new Harmony("local.scav.loadout.reroller.client");
        _harmony.Patch(AccessTools.Method(typeof(MatchMakerSideSelectionScreen), "Show", new[]
            {
                typeof(IEftSession), typeof(RaidSettings),
                typeof(EFT.HealthSystem.IHealthController), typeof(EFT.InventoryLogic.InventoryController)
            }), postfix: new HarmonyMethod(typeof(Plugin), nameof(SelectionScreenShown)));
        _harmony.Patch(AccessTools.Method(typeof(MatchMakerSideSelectionScreen), "Close"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SelectionScreenClosed)));
        _harmony.Patch(AccessTools.Method(typeof(MatchMakerSideSelectionScreen), "TranslateCommand"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BlockNavigationCommands)));
        Logger.LogInfo("Scav Loadout Reroller client loaded (SPT 4.1.x/Fika).");
    }

    internal void UpdateMenu()
    {
        if (!IsSideSelectionScreenOpen())
        {
            if (_overlay != null) _overlay.enabled = false;
            return;
        }
        _navigation?.Apply(_recovery);
    }

    internal void DrawMenu()
    {
        if ((!_showButton.Value && !_recovery.NeedsRefresh) || !IsSideSelectionScreenOpen() ||
            _inventoryPreview?.IsOpen == true) return;

        // UI rendered separately, without patching Fika / UI Fixes screens.
        if (_buttonStyle == null)
        {
            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
            _eyeTexture = EyeIcon.Create();
            _eyeContent = new GUIContent(_eyeTexture, "View SCAV inventory");
        }

        // Best-effort coordinates for the provided 1651x928 selection-screen screenshot.
        // Keep the existing IMGUI position; visibility follows the actual EFT screen.
        var width = Mathf.Clamp(Screen.width * 0.16f, 190f, 270f);
        var height = Mathf.Clamp(Screen.height * 0.032f, 30f, 42f);
        var x = Mathf.Clamp(Screen.width * _buttonCenterX.Value - width * 0.5f,
            5f, Mathf.Max(5f, Screen.width - width - 5f));
        var y = Mathf.Clamp(Screen.height * _buttonY.Value, 5f,
            Mathf.Max(5f, Screen.height - height - 5f));
        GUI.enabled = !_recovery.Busy;
        var buttonText = _recovery.Busy ? "Updating..." : "Reroll SCAV";
        if (GUI.Button(new Rect(x, y, width, height), buttonText, _buttonStyle))
        {
            _ = RunOperation();
        }
        GUI.enabled = !_recovery.Busy && !_recovery.NeedsRefresh;
        var eyeWidth = height + 10f;
        var eyeX = x + width + 8f;
        if (eyeX + eyeWidth > Screen.width - 5f) eyeX = Mathf.Max(5f, x - eyeWidth - 8f);
        if (GUI.Button(new Rect(eyeX, y, eyeWidth, height), _eyeContent, _buttonStyle))
            OpenScavInventory();
        GUI.enabled = true;
        if (!string.IsNullOrEmpty(_status))
        {
            // Display status below the game's scav description on the selection screen.
            var labelY = Mathf.Min(Screen.height - 62f, Screen.height * 0.755f);
            GUI.Label(new Rect(x - 60f, labelY, width + 120f, 62f), _status, _labelStyle);
        }
    }

    private void OpenScavInventory()
    {
        if (_recovery.Busy || _recovery.NeedsRefresh || !IsSideSelectionScreenOpen() || GameWorldPresent()) return;
        try
        {
            var scav = GetFikaSession()?.ProfileOfPet;
            if (scav?.Inventory == null)
                throw new InvalidOperationException("Player scav inventory is not ready.");
            if (_inventoryPreview == null) _inventoryPreview = new ScavInventoryPreview();
            _inventoryPreview.Open(scav);
            Logger.LogInfo("Opened a detached preview of the current scav inventory.");
        }
        catch (Exception ex)
        {
            _status = "Inventory preview unavailable. See BepInEx log.";
            Logger.LogError(ex);
        }
    }

    private async Task RunOperation()
    {
        if (!_recovery.TryBegin()) return;
        var operationId = Guid.NewGuid().ToString("N").Substring(0, 12);
        var stage = "selection";
        using (var cancellation = new CancellationTokenSource())
        {
            _operationCancellation = cancellation;
            var screenVersion = _screenVersion;
            try
            {
                _inventoryPreview?.Close();
                var session = GetFikaSession();
                var screen = _sideSelectionScreen;
                if (session == null || screen == null || !IsSideSelectionScreenOpen())
                    throw new InvalidOperationException("Character selection session is not ready.");
                if (!ReferenceEquals(_recoverySession, session))
                {
                    _recovery.ResetForSession();
                    _recoverySession = session;
                }
                if (_navigation == null) _navigation = new SelectionNavigationGuard(screen);
                _navigation.Apply(_recovery);
                _status = "Contacting SPT server...";
                stage = "reroll-request";
                _recovery.GenerationRequested();
                // Every click generates once, then loads that scav. The
                // transport must not replay a timed-out generation request.
                var result = await OperationDeadline.Wait(session.Send<RerollReply>(new SendRequest
                {
                    Url = RequestHandler.Host.TrimEnd('/') + "/scavreroller/reroll",
                    ParseInBackground = true,
                    TimeoutSeconds = 15,
                    Retries = SendRequest.NoRetries,
                    HandlingMode = ERequestHandlingMode.AbortOnFail
                }), TimeSpan.FromSeconds(20), cancellation.Token);
                if (result == null || (!result.ok && string.IsNullOrWhiteSpace(result.message)))
                    throw new InvalidOperationException("Invalid reroll response.");
                if (!result.ok)
                {
                    _recovery.GenerationRejected();
                    _status = result.message;
                    Logger.LogWarning("Reroll rejected [" + operationId + "]: " + _status);
                    return;
                }

                _status = "Loading scav profile and preview...";
                var refresh = new ScavProfileRefresh(session, screen);
                await refresh.RefreshAsync(cancellation.Token,
                    () => _screenVersion == screenVersion && ReferenceEquals(_sideSelectionScreen, screen) &&
                        IsSideSelectionScreenOpen(),
                    value => stage = value, () => _recovery.ProfileApplied());
                _status = "Scav updated. Ready to raid.";
                Logger.LogInfo("Scav profile and preview refreshed [" + operationId + "].");
            }
            catch (OperationCanceledException)
            {
                _status = "Update cancelled. Click Reroll SCAV to try again.";
                Logger.LogInfo("Scav operation cancelled [" + operationId + "] at " + stage + ".");
            }
            catch (Exception ex)
            {
                _status = "Update failed (" + stage + "). Click Reroll SCAV to try again.";
                RecordFailure(operationId, stage, ex);
            }
            finally
            {
                cancellation.Cancel();
                if (ReferenceEquals(_operationCancellation, cancellation)) _operationCancellation = null;
                _recovery.End();
                _navigation?.Apply(_recovery);
            }
        }
    }

    private void RecordFailure(string operationId, string stage, Exception error)
    {
        var detail = "[" + DateTime.UtcNow.ToString("O") + "] operation=" + operationId +
            " stage=" + stage + Environment.NewLine + error + Environment.NewLine;
        Logger.LogError(detail);
        try
        {
            File.AppendAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "ScavReroller-errors.log"), detail);
        }
        catch (Exception logError)
        {
            Logger.LogWarning("Unable to append persistent reroll error log: " + logError.Message);
        }
    }

    private bool IsSideSelectionScreenOpen()
    {
        return _sideSelectionScreen != null && _sideSelectionScreen.gameObject.activeInHierarchy;
    }

    private static void SelectionScreenShown(MatchMakerSideSelectionScreen __instance)
    {
        if (_instance == null || _instance._overlay == null) return;
        _instance._sideSelectionScreen = __instance;
        _instance._screenVersion++;
        _instance._navigation?.Dispose();
        _instance._navigation = new SelectionNavigationGuard(__instance);
        _instance._navigation.Apply(_instance._recovery);
        _instance._overlay.enabled = true;
    }

    private static void SelectionScreenClosed(MatchMakerSideSelectionScreen __instance)
    {
        if (_instance == null || !ReferenceEquals(_instance._sideSelectionScreen, __instance)) return;
        _instance._screenVersion++;
        _instance._operationCancellation?.Cancel();
        _instance._navigation?.Dispose();
        _instance._navigation = null;
        _instance._inventoryPreview?.Close();
        if (_instance._overlay != null) _instance._overlay.enabled = false;
        _instance._sideSelectionScreen = null;
    }

    internal static bool GameWorldPresent() => Singleton<GameWorld>.Instantiated;

    private static bool BlockNavigationCommands(ECommand __0, ref InputNode.ETranslateResult __result)
    {
        // Escape always remains usable. Closing selection cancels local work
        // and prevents a late HTTP response from being applied to another screen.
        if (__0.IsCommand(ECommand.Escape) || _instance == null || !_instance._recovery.Busy) return true;
        __result = InputNode.ETranslateResult.BlockAll;
        return false;
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
        _operationCancellation?.Cancel();
        _navigation?.Dispose();
        _inventoryPreview?.Dispose();
        if (_eyeTexture != null) Object.Destroy(_eyeTexture);
        if (_overlay != null)
        {
            _overlay.Owner = null;
            Object.Destroy(_overlay);
        }
        if (_instance == this) _instance = null;
    }

    internal static ClientBackendSession? GetFikaSession()
    {
        if (_getFikaSession == null)
        {
            var fika = FindType("Fika.Core.Main.Utils.FikaGlobals");
            _getFikaSession = fika?.GetMethod("GetSession", BindingFlags.Static | BindingFlags.Public);
        }
        return _getFikaSession?.Invoke(null, null) as ClientBackendSession;
    }

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName, false);
            if (type != null) return type;
        }
        return null;
    }

    [Serializable]
    internal sealed class RerollReply
    {
        // Populated by the game's backend deserializer.
        public bool ok = false;
        public string message = string.Empty;
    }
}
