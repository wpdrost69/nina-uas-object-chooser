using System.ComponentModel.Composition;
using System.Windows;

namespace UnderAfricanSkies.ObjectChooser {

    /// <summary>
    /// Exports the dockable panel's DataTemplate ResourceDictionary to NINA so the
    /// panel UI is discovered and rendered. NINA merges every exported
    /// ResourceDictionary into its application resources.
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class ObjectChooserTemplates : ResourceDictionary {
        public ObjectChooserTemplates() {
            InitializeComponent();
        }
    }
}
