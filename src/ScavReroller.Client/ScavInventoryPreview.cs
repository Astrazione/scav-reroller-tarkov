using System;
using System.Reflection;
using EFT;
using EFT.UI;

namespace ScavReroller.Client;

/// <summary>Uses the stock equipment window with a detached, fully searched inventory.</summary>
internal sealed class ScavInventoryPreview : IDisposable
{
    private static readonly FieldInfo WindowField = typeof(ItemUiContext).GetField("_playerEquipmentWindow",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(ItemUiContext).FullName, "_playerEquipmentWindow");
    private PlayerEquipmentWindowContext? _context;

    internal bool IsOpen => _context != null;

    internal void Open(Profile scav)
    {
        Close();
        var ui = ItemUiContext.Instance;
        if (ui == null) throw new InvalidOperationException("Inventory UI is not ready.");
        var window = WindowField.GetValue(ui) as PlayerEquipmentWindow;
        if (window == null) throw new InvalidOperationException("Equipment window is not available.");

        // PlayerEquipmentWindow assigns item addresses to a display controller.
        // Never give it the session's live equipment. Profile.Clone performs a
        // descriptor round trip with FullySearchedSearchController.Instance.
        var snapshot = scav.Clone();
        if (ReferenceEquals(snapshot.Inventory, scav.Inventory) ||
            ReferenceEquals(snapshot.Inventory.Equipment, scav.Inventory.Equipment))
            throw new InvalidOperationException("Inventory preview requires a detached profile.");
        if (snapshot.Inventory.DeserializationErrors.Count != 0)
        {
            ItemFactory.LogErrors(snapshot.Inventory.DeserializationErrors, "scav inventory preview");
            throw new InvalidOperationException("Preview inventory could not be loaded. See the game log.");
        }

        // false keeps all container contents; the visible-equipment-only clone
        // used for 3D models would omit items that this window needs to show.
        var visual = snapshot.GetVisualEquipmentState(false);
        var player = new GroupPlayer
        {
            Id = snapshot.Id,
            AccountId = snapshot.AccountId,
            Info = visual.Info,
            PlayerVisualRepresentation = visual
        };

        try
        {
            _context = ui.ShowPlayerEquipmentWindow(player, snapshot);
            _context.OnClose += HandleClosed;
        }
        catch
        {
            // Native Show opens the frame before it finishes building slots.
            // Clean up even if it throws before returning a window context.
            window.Close();
            _context = null;
            throw;
        }
    }

    internal void Close()
    {
        var context = _context;
        _context = null;
        if (context == null) return;
        context.OnClose -= HandleClosed;
        context.Close();
    }

    private void HandleClosed() => _context = null;

    public void Dispose() => Close();
}
