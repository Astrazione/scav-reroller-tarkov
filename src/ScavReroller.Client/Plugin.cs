using System;
using System.Reflection;
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
[BepInPlugin("local.scav.loadout.reroller.client", "Scav Loadout Reroller", "0.1.8")]
[BepInDependency("com.SPT.core")]
public sealed class Plugin : BaseUnityPlugin
{
    private ConfigEntry<bool> _showButton = null!;
    private ConfigEntry<float> _buttonCenterX = null!;
    private ConfigEntry<float> _buttonY = null!;
    private bool _pending;
    private bool _refreshRequired;
    private ScavProfileRefresh? _refresh;
    private Harmony? _harmony;
    private static Plugin? _instance;
    private string _status = "";
    private GUIStyle? _buttonStyle;
    private GUIStyle? _labelStyle;
    private MatchMakerSideSelectionScreen? _sideSelectionScreen;
    private MenuOverlay? _overlay;
    private static MethodInfo? _getFikaSession;

    private void Awake()
    {
        _instance = this;
        _showButton = Config.Bind("UI", "ShowButton", true,
            "Show Scav Reroll only on the character selection screen.");
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
        _refresh?.KeepNavigationBlocked();
    }

    internal void DrawMenu()
    {
        if ((!_showButton.Value && !_refreshRequired) || !IsSideSelectionScreenOpen()) return;

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
        }

        // Best-effort coordinates for the provided 1651x928 selection-screen screenshot.
        // Keep the existing IMGUI position; visibility follows the actual EFT screen.
        var width = Mathf.Clamp(Screen.width * 0.16f, 190f, 270f);
        var height = Mathf.Clamp(Screen.height * 0.032f, 30f, 42f);
        var x = Mathf.Clamp(Screen.width * _buttonCenterX.Value - width * 0.5f,
            5f, Mathf.Max(5f, Screen.width - width - 5f));
        var y = Mathf.Clamp(Screen.height * _buttonY.Value, 5f,
            Mathf.Max(5f, Screen.height - height - 5f));
        GUI.enabled = !_pending;
        var buttonText = _pending ? "Updating..." : _refreshRequired ? "Retry refresh" : "Reroll SCAV";
        if (GUI.Button(new Rect(x, y, width, height), buttonText, _buttonStyle))
        {
            _ = Reroll();
        }
        GUI.enabled = true;
        if (!string.IsNullOrEmpty(_status))
        {
            // Display status below the game's scav description on the selection screen.
            var labelY = Mathf.Min(Screen.height - 62f, Screen.height * 0.755f);
            GUI.Label(new Rect(x - 60f, labelY, width + 120f, 62f), _status, _labelStyle);
        }
    }

    private async Task Reroll()
    {
        if (_pending) return;
        _pending = true;
        try
        {
            var session = GetFikaSession();
            var screen = _sideSelectionScreen as MatchMakerSideSelectionScreen;
            if (session == null || screen == null || !IsSideSelectionScreenOpen())
                throw new InvalidOperationException("Character selection session is not ready.");
            if (_refresh != null && !_refresh.Matches(session, screen))
            {
                _refresh.Dispose();
                _refresh = null;
            }
            if (_refresh == null) _refresh = new ScavProfileRefresh(session, screen);

            if (!_refreshRequired)
            {
                _status = "Contacting SPT server...";
                // Until the reply is known, a timeout may mean the server has
                // already applied the reroll. Recovery only reloads profiles.
                _refreshRequired = true;
                var body = await RequestHandler.PostJsonAsync("/scavreroller/reroll", "{}");
                var result = JsonUtility.FromJson<RerollReply>(body);
                if (result == null) throw new InvalidOperationException("Empty reroll response.");
                if (!result.ok)
                {
                    // A well-formed explicit rejection did not change the profile.
                    // Unknown/malformed replies still require a profile refresh.
                    if (string.IsNullOrWhiteSpace(result.message))
                        throw new InvalidOperationException("Invalid reroll response.");
                    _refreshRequired = false;
                    _status = result.message;
                    Logger.LogWarning("Scav reroll rejected: " + _status);
                    return;
                }
            }

            _status = "Loading new scav profile and preview...";
            await _refresh.RefreshAsync();
            _refreshRequired = false;
            _status = "Scav updated. Ready to raid.";
            Logger.LogInfo("Scav session profile and preview refreshed; PMC profile preserved.");
        }
        catch (Exception ex)
        {
            _status = _refreshRequired
                ? "Refresh failed. Click Retry refresh before continuing."
                : "Reroll unavailable: " + ex.Message;
            Logger.LogError(ex);
        }
        finally
        {
            _pending = false;
            if (!_refreshRequired)
            {
                _refresh?.Dispose();
                _refresh = null;
            }
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
        _instance._overlay.enabled = true;
    }

    private static void SelectionScreenClosed(MatchMakerSideSelectionScreen __instance)
    {
        if (_instance == null || !ReferenceEquals(_instance._sideSelectionScreen, __instance)) return;
        if (_instance._overlay != null) _instance._overlay.enabled = false;
        _instance._sideSelectionScreen = null;
    }

    internal static bool GameWorldPresent() => Singleton<GameWorld>.Instantiated;

    private static bool BlockNavigationCommands(ref InputNode.ETranslateResult __result)
    {
        if (_instance == null || (!_instance._pending && !_instance._refreshRequired)) return true;
        __result = InputNode.ETranslateResult.BlockAll;
        return false;
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
        _refresh?.Dispose();
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
    private sealed class RerollReply
    {
        // JsonUtility populates these public fields during deserialization.
        public bool ok = false;
        public string message = string.Empty;
    }
}
