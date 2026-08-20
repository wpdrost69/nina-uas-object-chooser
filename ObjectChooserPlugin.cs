using System.ComponentModel.Composition;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

namespace UnderAfricanSkies.ObjectChooser {

    /// <summary>
    /// Plugin entry point. NINA discovers this via MEF ([Export]) and reads the
    /// plugin's name/version/author/description from the assembly metadata attributes
    /// defined in Properties/AssemblyInfo.cs.
    ///
    /// This class stays intentionally tiny: all the real work lives in the dockable
    /// panel (ObjectChooserDockable), which NINA also discovers via [Export].
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class ObjectChooserPlugin : PluginBase {

        [ImportingConstructor]
        public ObjectChooserPlugin() {
            // Nothing to initialise yet. Plugin settings/options can be added here later
            // (e.g. an override for the catalogue URL) using a Settings resource + an
            // IPluginOptionsVM export.
        }
    }
}
