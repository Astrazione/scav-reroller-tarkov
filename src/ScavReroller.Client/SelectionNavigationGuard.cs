using System;
using System.Reflection;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;

namespace ScavReroller.Client;

internal sealed class SelectionNavigationGuard : IDisposable
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo SideField = RequiredField("_sideType");
    private readonly MatchMakerSideSelectionScreen _screen;
    private readonly DefaultUIButton _next;
    private bool _nextLocked;
    private bool _nextWasEnabled;

    internal SelectionNavigationGuard(MatchMakerSideSelectionScreen screen)
    {
        _screen = screen;
        _next = (DefaultUIButton)RequiredField("_nextButton").GetValue(screen);
    }

    internal void Apply(RerollRecoveryState state)
    {
        var scavSelected = (ESideType)SideField.GetValue(_screen) != ESideType.Pmc;
        SetLocked(_next, state.BlocksNext(scavSelected), ref _nextLocked, ref _nextWasEnabled);
    }

    public void Dispose()
    {
        SetLocked(_next, false, ref _nextLocked, ref _nextWasEnabled);
    }

    private static void SetLocked(DefaultUIButton button, bool shouldLock, ref bool owned, ref bool wasEnabled)
    {
        if (button == null) return;
        if (shouldLock)
        {
            if (!owned) { wasEnabled = button.Interactable; owned = true; }
            button.Interactable = false;
        }
        else if (owned)
        {
            button.Interactable = wasEnabled;
            owned = false;
        }
    }

    private static FieldInfo RequiredField(string name) =>
        typeof(MatchMakerSideSelectionScreen).GetField(name, Fields)
        ?? throw new MissingFieldException(typeof(MatchMakerSideSelectionScreen).FullName, name);
}
