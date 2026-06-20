using VL.Core.Import;

// Expose every public type under the VL.Agent namespace as nodes in the
// "Agent" category. (ImportAsIs lives in VL.Core.dll.)
[assembly: ImportAsIs(Namespace = "VL.Agent", Category = "Agent")]
