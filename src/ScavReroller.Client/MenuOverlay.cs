using UnityEngine;

namespace ScavReroller.Client;

/// <summary>Enabled by selection-screen Show and disabled by Close, never polled in raids.</summary>
internal sealed class MenuOverlay : MonoBehaviour
{
    internal Plugin? Owner;

    private void Update()
    {
        if (Owner != null) Owner.UpdateMenu();
    }

    private void OnGUI()
    {
        if (Owner != null) Owner.DrawMenu();
    }
}
