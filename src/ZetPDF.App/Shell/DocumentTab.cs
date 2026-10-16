using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZetPDF.Core.Contracts;

namespace ZetPDF.App.Shell;

/// <summary>Model karty dokumentu (nagłówek w pasku kart + widok z IDocumentViewFactory).</summary>
public sealed class DocumentTab : ObservableObject
{
    private string _title;
    private bool _isModified;
    private string _toolTip;

    public DocumentTab(IPdfDocumentSession session, IDocumentView view, Func<DocumentTab, Task> close)
    {
        Session = session;
        View = view;
        _title = session.DisplayName;
        _toolTip = session.FilePath ?? session.DisplayName;
        CloseCommand = new AsyncRelayCommand(() => close(this));
        Refresh();
    }

    public IPdfDocumentSession Session { get; }
    public IDocumentView View { get; }
    public FrameworkElement Element => View.Element;
    public ICommand CloseCommand { get; }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public bool IsModified
    {
        get => _isModified;
        private set => SetProperty(ref _isModified, value);
    }

    public string ToolTip
    {
        get => _toolTip;
        private set => SetProperty(ref _toolTip, value);
    }

    public void Refresh()
    {
        Title = Session.DisplayName;
        IsModified = Session.IsModified;
        ToolTip = Session.FilePath ?? Session.DisplayName;
    }
}
