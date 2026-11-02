$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Exercise the production replacement helper without loading EFT or profiles.
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/ScavReroller.Client/ProfileSlots.cs') -Raw
$checks = @'
namespace ScavReroller.Client
{
    public static class ProfileSlotChecks
    {
        public static string Run()
        {
            var checks = 0;
            for (var size = 2; size <= 12; size++)
            {
                for (var scavIndex = 0; scavIndex < size; scavIndex++)
                {
                    var profiles = new object[size];
                    for (var i = 0; i < size; i++) profiles[i] = new object();
                    var before = (object[])profiles.Clone();
                    var newScav = new object();
                    var result = ProfileSlots.ReplaceSingleReference(profiles, profiles[scavIndex], newScav);
                    Require(!object.ReferenceEquals(result, profiles), "Input array was reused.");
                    for (var i = 0; i < size; i++)
                    {
                        Require(object.ReferenceEquals(profiles[i], before[i]), "Original session array was mutated.");
                        Require(object.ReferenceEquals(result[i], i == scavIndex ? newScav : before[i]),
                            "PMC or another profile was replaced.");
                    }
                    // A second refresh replaces the current scav and preserves others again.
                    var nextScav = new object();
                    var next = ProfileSlots.ReplaceSingleReference(result, newScav, nextScav);
                    Require(object.ReferenceEquals(next[scavIndex], nextScav), "Repeated refresh failed.");
                    Require(object.ReferenceEquals(result[scavIndex], newScav), "Previous refresh was mutated.");
                    checks++;
                }
            }

            var pmc = new object();
            var scav = new object();
            ExpectFailure(new[] { pmc }, scav);
            ExpectFailure(new object[0], scav);
            ExpectFailure(new[] { pmc, scav, scav }, scav);
            return (checks + 3) + " profile isolation and rejection scenarios passed.";
        }

        private static void ExpectFailure(object[] profiles, object scav)
        {
            var before = (object[])profiles.Clone();
            var rejected = false;
            try { ProfileSlots.ReplaceSingleReference(profiles, scav, new object()); }
            catch (System.InvalidOperationException) { rejected = true; }
            Require(rejected, "Invalid session array was accepted.");
            for (var i = 0; i < profiles.Length; i++)
                Require(object.ReferenceEquals(before[i], profiles[i]), "Failed refresh mutated the session.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new System.Exception(message);
        }
    }
}
'@
Add-Type -TypeDefinition ($source + [Environment]::NewLine + $checks) -Language CSharp
[ScavReroller.Client.ProfileSlotChecks]::Run()
