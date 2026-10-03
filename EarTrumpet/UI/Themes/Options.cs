using System.Windows;

namespace EarTrumpet.UI.Themes
{
    public class Options
    {
        public enum SourceKind
        {
            App, System
        }

        public static SourceKind? GetSource(DependencyObject obj) => (SourceKind?)obj.GetValue(SourceProperty);
        public static void SetSource(DependencyObject obj, SourceKind? value) => obj.SetValue(SourceProperty, value);
        public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached("Source", typeof(SourceKind?), typeof(Options), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, SourceChanged));

        // A theme binding created before Source has inherited from the parent cannot resolve yet and
        // has to be applied again once it does (GitHub #13). Driving that from the property metadata
        // keeps it free: the alternative, DependencyPropertyDescriptor.AddValueChanged, roots every
        // subscribing element in a static table for the life of the process.
        private static void SourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Brush.ReapplyBindings(d);

        public static string GetScope(DependencyObject obj) => (string)obj.GetValue(ScopeProperty);
        public static void SetScope(DependencyObject obj, string value) => obj.SetValue(ScopeProperty, value);
        public static readonly DependencyProperty ScopeProperty =
        DependencyProperty.RegisterAttached("Scope", typeof(string), typeof(Options), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.Inherits));
    }
}
