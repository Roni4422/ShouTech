using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>Company-scoped attachment browser for purchase documents.</summary>
public sealed class PurchaseAttachmentsWin : BaseWin
{
    private readonly Guid _companyId;
    private readonly long _documentId;
    private readonly string _actor;
    private readonly DataGrid _grid=new();
    private readonly ObservableCollection<AttachmentRow> _rows=new();
    private readonly Button _upload=new(),_open=new(),_delete=new(),_refresh=new(),_close=new();

    public PurchaseAttachmentsWin(Guid companyId,long documentId,string actor)
    {
        _companyId=companyId;_documentId=documentId;_actor=actor;
        Title=T("PurchaseAttachments.Title");Width=850;Height=520;MinWidth=650;MinHeight=380;
        BuildUi();Loaded+=async(_,_)=>await RefreshAsync();
    }
    private void BuildUi()
    {
        var root=new DockPanel{Margin=new Thickness(12)};
        var bar=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,0,0,8)};
        StyleButton(_upload,"PurchaseAttachments.Upload");StyleButton(_open,"PurchaseAttachments.OpenFile");StyleButton(_delete,"PurchaseAttachments.Delete");StyleButton(_refresh,"PurchaseAttachments.Refresh");StyleButton(_close,"PurchaseAttachments.Close");
        _upload.Click+=async(_,_)=>await UploadAsync();_open.Click+=async(_,_)=>await OpenAsync();_delete.Click+=async(_,_)=>await DeleteAsync();_refresh.Click+=async(_,_)=>await RefreshAsync();_close.Click+=(_,_)=>Close();
        foreach(var b in new[]{_upload,_open,_delete,_refresh,_close})bar.Children.Add(b);
        DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);
        _grid.AutoGenerateColumns=false;_grid.IsReadOnly=true;_grid.CanUserAddRows=false;_grid.SelectionMode=DataGridSelectionMode.Single;_grid.SelectionUnit=DataGridSelectionUnit.FullRow;_grid.ItemsSource=_rows;
        _grid.Columns.Add(new DataGridTextColumn{Header=T("PurchaseAttachments.FileName"),Binding=new Binding(nameof(AttachmentRow.Name)),Width=new DataGridLength(2,DataGridLengthUnitType.Star)});
        _grid.Columns.Add(new DataGridTextColumn{Header=T("PurchaseAttachments.Size"),Binding=new Binding(nameof(AttachmentRow.Size)),Width=100});
        _grid.Columns.Add(new DataGridTextColumn{Header=T("PurchaseAttachments.UploadedBy"),Binding=new Binding(nameof(AttachmentRow.UploadedBy)),Width=130});
        _grid.Columns.Add(new DataGridTextColumn{Header=T("PurchaseAttachments.UploadedAt"),Binding=new Binding(nameof(AttachmentRow.UploadedAt)),Width=170});
        root.Children.Add(_grid);Content=root;
        _upload.IsEnabled=AppHost.Permissions.Has("PUR.ATTACH.UPLOAD");_delete.IsEnabled=AppHost.Permissions.Has("PUR.ATTACH.DELETE");
        ApplyLocalizedText();
    }
    private static void StyleButton(Button button,string key){button.Content=T(key);button.MinWidth=78;button.Margin=new Thickness(4,0,0,0);button.Padding=new Thickness(8,4,8,4);}

    protected override void ApplyLocalizedText()
    {
        Title = T("PurchaseAttachments.Title");
        StyleButton(_upload, "PurchaseAttachments.Upload");
        StyleButton(_open, "PurchaseAttachments.OpenFile");
        StyleButton(_delete, "PurchaseAttachments.Delete");
        StyleButton(_refresh, "PurchaseAttachments.Refresh");
        StyleButton(_close, "PurchaseAttachments.Close");
        if (_grid.Columns.Count == 4)
        {
            _grid.Columns[0].Header = T("PurchaseAttachments.FileName");
            _grid.Columns[1].Header = T("PurchaseAttachments.Size");
            _grid.Columns[2].Header = T("PurchaseAttachments.UploadedBy");
            _grid.Columns[3].Header = T("PurchaseAttachments.UploadedAt");
        }
    }

    private async Task RefreshAsync()
    {
        var service=AppHost.PurchaseAttachments;if(service is null){MessageBox.Show(this,T("PurchaseAttachments.MissingService"),Title);return;}
        try{_rows.Clear();foreach(var a in await service.ListAsync(_companyId,_documentId)){_rows.Add(new(a.Id,a.OriginalName,$"{a.ByteLength/1024d:N1} KB",a.UploadedBy,a.UploadedAt.ToLocalTime().ToString("g")));}}
        catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private async Task UploadAsync()
    {
        var service=AppHost.PurchaseAttachments;if(service is null)return;
        var dlg=new OpenFileDialog{Title=T("PurchaseAttachments.Upload"),Filter=T("PurchaseAttachments.UploadFilter")+"|*.pdf;*.png;*.jpg;*.jpeg;*.xlsx;*.docx;*.csv|All files|*.*",Multiselect=false};
        if(dlg.ShowDialog(this)!=true)return;
        try{await using var stream=File.OpenRead(dlg.FileName);await service.UploadAsync(_companyId,_documentId,Path.GetFileName(dlg.FileName),"application/octet-stream",stream,_actor);await RefreshAsync();}
        catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private async Task OpenAsync()
    {
        if(_grid.SelectedItem is not AttachmentRow row){MessageBox.Show(this,T("PurchaseAttachments.NoSelection"),Title);return;}
        var service=AppHost.PurchaseAttachments;if(service is null)return;
        try{await using var source=await service.OpenReadAsync(_companyId,row.Id,_actor);var ext=Path.GetExtension(row.Name);var path=Path.Combine(Path.GetTempPath(),$"shoutech-{Guid.NewGuid():N}{ext}");await using(var target=File.Create(path))await source.CopyToAsync(target);Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
        catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private async Task DeleteAsync()
    {
        if(_grid.SelectedItem is not AttachmentRow row){MessageBox.Show(this,T("PurchaseAttachments.NoSelection"),Title);return;}
        if(MessageBox.Show(this,T("PurchaseAttachments.ConfirmDelete"),Title,MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        var service=AppHost.PurchaseAttachments;if(service is null)return;
        try{await service.DeleteAsync(_companyId,row.Id,_actor);await RefreshAsync();}
        catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private sealed record AttachmentRow(Guid Id,string Name,string Size,string UploadedBy,string UploadedAt);
}
