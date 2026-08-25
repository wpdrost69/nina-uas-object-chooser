using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using UnderAfricanSkies.ObjectChooser.Models;

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

        /// <summary>
        /// A list row was realized (scrolled into view under virtualization). Load just that object's
        /// thumbnail — this is what makes thumbnail loading lazy (only visible rows fetch), instead of
        /// firing a request for the whole catalogue when the panel opens.
        /// </summary>
        private void OnObjectRowLoaded(object sender, RoutedEventArgs e) {
            if (!(sender is FrameworkElement fe)) return;
            if (!(fe.DataContext is AstroObject o)) return;
            var vm = FindVM(fe);
            vm?.EnsureThumbnail(o);
        }

        private static ObjectChooserDockable FindVM(DependencyObject start) {
            var d = start;
            while (d != null) {
                if (d is FrameworkElement fe && fe.DataContext is ObjectChooserDockable vm) return vm;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }
    }
}
