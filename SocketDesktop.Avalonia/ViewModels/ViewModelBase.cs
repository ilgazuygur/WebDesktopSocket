using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SocketDesktop.Avalonia.ViewModels;

// A tiny INotifyPropertyChanged base class so the views can bind to view
// model properties and update automatically when they change. Hand-rolled
// on purpose - it's a few lines, and avoids pulling in a whole MVVM
// framework just for change notification.
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    // Sets a backing field and raises PropertyChanged only if the value
    // actually changed. Returns true if it changed, so callers can chain
    // extra work (like updating a derived property).
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
