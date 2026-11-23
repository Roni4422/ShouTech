using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>Internal purchase request editor, sequential approvals, and conversion to a linked purchase-order draft.</summary>
public sealed class PurchaseRequisitionWin : BaseWin
{
    private Guid _companyId;
    private long? _documentId;
    private readonly TextBox _number=new(),_department=new(),_notes=new(),_approvers=new(),_reason=new(),_supplierId=new(),_supplierName=new(),_currency=new(){Text="USD"},_rate=new(){Text="1"},_warehouse=new();
    private readonly DatePicker _date=new(){SelectedDate=DateTime.Today};
    private readonly DataGrid _lines=new(),_approvals=new();
    private readonly ObservableCollection<LineVm> _lineRows=new();
    private readonly ObservableCollection<ApprovalVm> _approvalRows=new();
    private readonly TextBlock _status=new();
    private readonly Button _new=new(),_load=new(),_save=new(),_submit=new(),_approve=new(),_reject=new(),_convert=new(),_close=new();

    public PurchaseRequisitionWin()
    {
        Title=T("PurchaseRequisition.Title");Width=1200;Height=760;MinWidth=900;MinHeight=600;
        _lines.ItemsSource=_lineRows;_approvals.ItemsSource=_approvalRows;BuildUi();Loaded+=async(_,_)=>await InitializeAsync();
    }
    private async Task InitializeAsync()
    {
        if(AppHost.PurchaseRequisitions is null||AppHost.Orders is null||AppHost.JournalStore is null){MessageBox.Show(this,T("PurchaseRequisition.ServiceUnavailable"),Title);Close();return;}
        if(!AppHost.Permissions.Has("PUR.REQ.VIEW")&&!AppHost.Permissions.Has("PUR.REQ.EDIT")&&!AppHost.Permissions.Has("PUR.REQ.APPROVE")){MessageBox.Show(this,T("PurchaseRequisition.PermissionDenied"),Title);Close();return;}
        try{_companyId=await AppHost.JournalStore.ResolveCompanyIdAsync(CurrentCompanyCode);if(_companyId==Guid.Empty)throw new InvalidOperationException("Company context is unavailable.");await NewAsync();}
        catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);Close();}
    }
    private void BuildUi()
    {
        var root=new DockPanel{Margin=new Thickness(12)};
        var toolbar=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,0,0,8)};
        Style(_new,"PurchaseRequisition.New");Style(_load,"PurchaseRequisition.Load");Style(_save,"PurchaseRequisition.Save");Style(_submit,"PurchaseRequisition.Submit");Style(_approve,"PurchaseRequisition.Approve");Style(_reject,"PurchaseRequisition.Reject");Style(_convert,"PurchaseRequisition.Convert");Style(_close,"PurchaseRequisition.Close");
        _new.Click+=async(_,_)=>await NewAsync();_load.Click+=async(_,_)=>await LoadAsync();_save.Click+=async(_,_)=>await SaveAsync();_submit.Click+=async(_,_)=>await SubmitAsync();_approve.Click+=async(_,_)=>await DecideAsync(true);_reject.Click+=async(_,_)=>await DecideAsync(false);_convert.Click+=async(_,_)=>await ConvertAsync();_close.Click+=(_,_)=>Close();
        foreach(var b in new[]{_new,_load,_save,_submit,_approve,_reject,_convert,_close})toolbar.Children.Add(b);DockPanel.SetDock(toolbar,Dock.Top);root.Children.Add(toolbar);
        var statusBar=new Border{Child=_status,Padding=new Thickness(5),BorderThickness=new Thickness(0,1,0,0)};DockPanel.SetDock(statusBar,Dock.Bottom);root.Children.Add(statusBar);
        var body=new Grid();body.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});body.RowDefinitions.Add(new RowDefinition{Height=new GridLength(2,GridUnitType.Star)});body.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});body.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        var header=new Grid{Margin=new Thickness(0,0,0,8)};for(int i=0;i<4;i++)header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});for(int i=0;i<2;i++)header.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        AddField(header,0,0,"PurchaseRequisition.Number",_number);AddField(header,1,0,"PurchaseRequisition.Date",_date);AddField(header,2,0,"PurchaseRequisition.Department",_department);AddField(header,3,0,"PurchaseRequisition.Currency",_currency);AddField(header,0,1,"PurchaseRequisition.ExchangeRate",_rate);AddField(header,1,1,"PurchaseRequisition.Warehouse",_warehouse);AddField(header,2,1,"PurchaseRequisition.Notes",_notes);AddField(header,3,1,"PurchaseRequisition.Approvers",_approvers);Grid.SetRow(header,0);body.Children.Add(header);
        ConfigureGrid(_lines);AddCol(_lines,"PurchaseRequisition.LineNo",nameof(LineVm.LineNo),55);AddCol(_lines,"PurchaseRequisition.MaterialId",nameof(LineVm.MaterialId),85);AddCol(_lines,"PurchaseRequisition.MaterialCode",nameof(LineVm.MaterialCode),100);AddCol(_lines,"PurchaseRequisition.Description",nameof(LineVm.Description),2);AddCol(_lines,"PurchaseRequisition.Unit",nameof(LineVm.Unit),65);AddCol(_lines,"PurchaseRequisition.Quantity",nameof(LineVm.Quantity),85);AddCol(_lines,"PurchaseRequisition.EstimatedPrice",nameof(LineVm.EstimatedPrice),110);Grid.SetRow(_lines,1);body.Children.Add(_lines);
        var decision=new Grid{Margin=new Thickness(0,8,0,8)};decision.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(2,GridUnitType.Star)});decision.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});decision.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});AddField(decision,0,0,"PurchaseRequisition.Reason",_reason);AddField(decision,1,0,"PurchaseRequisition.SupplierId",_supplierId);AddField(decision,2,0,"PurchaseRequisition.SupplierName",_supplierName);Grid.SetRow(decision,2);body.Children.Add(decision);
        ConfigureGrid(_approvals);AddCol(_approvals,"PurchaseRequisition.ApprovalStep",nameof(ApprovalVm.StepNo),65);AddCol(_approvals,"PurchaseRequisition.Approver",nameof(ApprovalVm.Approver),150);AddCol(_approvals,"PurchaseRequisition.ApprovalStatus",nameof(ApprovalVm.Status),100);AddCol(_approvals,"PurchaseRequisition.ApprovalReason",nameof(ApprovalVm.Reason),2);Grid.SetRow(_approvals,3);body.Children.Add(_approvals);
        root.Children.Add(body);Content=root;ApplyLocalizedText();
    }
    private static void Style(Button b,string key){b.Content=T(key);b.MinWidth=82;b.Margin=new Thickness(3);b.Padding=new Thickness(8,4,8,4);}
    private static void ConfigureGrid(DataGrid g){g.AutoGenerateColumns=false;g.CanUserAddRows=true;g.CanUserDeleteRows=true;g.HeadersVisibility=DataGridHeadersVisibility.Column;g.Margin=new Thickness(0,0,0,8);}
    private static void AddCol(DataGrid g,string key,string path,double width){g.Columns.Add(new DataGridTextColumn{Header=T(key),Binding=new Binding(path){UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=width==2?new DataGridLength(1,DataGridLengthUnitType.Star):new DataGridLength(width)});}
    private static void AddField(Grid g,int col,int row,string key,Control input){var panel=new StackPanel{Margin=new Thickness(4)};panel.Children.Add(new TextBlock{Text=T(key),Margin=new Thickness(0,0,0,3)});input.Margin=new Thickness(0,0,4,0);input.MinWidth=100;panel.Children.Add(input);Grid.SetColumn(panel,col);Grid.SetRow(panel,row);g.Children.Add(panel);}
    private async Task NewAsync(){_documentId=null;_number.Text=await AppHost.Orders!.NextDocNoAsync(CommercialDocSide.Purchase,_companyId,CommercialDocKinds.PurchaseRequisition);_date.SelectedDate=DateTime.Today;_department.Clear();_notes.Clear();_approvers.Clear();_reason.Clear();_supplierId.Clear();_supplierName.Clear();_lineRows.Clear();_lineRows.Add(new(){LineNo=1,Quantity=1,EstimatedPrice=0});_approvalRows.Clear();_status.Text="Draft";}
    private CommercialDocumentDraft BuildDraft(){var currency=_currency.Text.Trim().ToUpperInvariant();if(!decimal.TryParse(_rate.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out var rate)||rate<=0)throw new InvalidOperationException(T("PurchaseRequisition.InvalidInput"));var lines=_lineRows.Where(x=>!string.IsNullOrWhiteSpace(x.Description)).Select((x,i)=>new CommercialLineDraft(ParseLong(x.MaterialId),x.MaterialCode,x.Description,x.Unit,ParseDecimal(x.Quantity),ParseDecimal(x.EstimatedPrice),0,0,ParseLong(_warehouse.Text),i+1)).ToArray();if(lines.Length==0)throw new InvalidOperationException(T("PurchaseRequisition.InvalidInput"));var notes=string.IsNullOrWhiteSpace(_department.Text)?_notes.Text:$"Department: {_department.Text.Trim()}\n{_notes.Text}";return new(_companyId,CommercialDocKinds.PurchaseRequisition,_number.Text.Trim(),DateOnly.FromDateTime(_date.SelectedDate??DateTime.Today),null,null,ParseLong(_warehouse.Text),currency,rate,notes,lines);}
    private async Task SaveAsync(){if(!AppHost.Permissions.Has("PUR.REQ.EDIT")){MessageBox.Show(this,T("PurchaseRequisition.PermissionDenied"),Title);return;}try{var draft=BuildDraft();if(_documentId is null)_documentId=await AppHost.PurchaseRequisitions!.CreateDraftAsync(draft,CurrentUserId);else await AppHost.PurchaseRequisitions!.UpdateDraftAsync(_companyId,_documentId.Value,draft,CurrentUserId);_status.Text=T("PurchaseRequisition.Saved");}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async Task LoadAsync(){try{var doc=await AppHost.Orders!.FindByNumberAsync(CommercialDocSide.Purchase,_companyId,CommercialDocKinds.PurchaseRequisition,_number.Text.Trim());if(doc is null)throw new InvalidOperationException(T("PurchaseRequisition.NoDocument"));_documentId=doc.DocId;_number.Text=doc.DocNo;_date.SelectedDate=doc.DocDate.ToDateTime(TimeOnly.MinValue);_notes.Text=doc.Notes??"";_currency.Text=doc.CurrencyCode;_rate.Text=doc.ExchangeRate.ToString(CultureInfo.InvariantCulture);_lineRows.Clear();foreach(var x in doc.Lines)_lineRows.Add(new(){LineNo=x.LineNo,MaterialId=x.MaterialId?.ToString(CultureInfo.InvariantCulture)??"",MaterialCode=x.MaterialCode??"",Description=x.Description,Unit=x.Unit??"",Quantity=x.Qty.ToString(CultureInfo.InvariantCulture),EstimatedPrice=x.UnitPrice.ToString(CultureInfo.InvariantCulture)});await RefreshWorkflowAsync();}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async Task SubmitAsync(){if(_documentId is null){await SaveAsync();if(_documentId is null)return;}if(!AppHost.Permissions.Has("PUR.REQ.EDIT"))return;var users=_approvers.Text.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);var steps=users.Select((u,i)=>new PurchaseRequisitionApprovalStep(i+1,u)).ToArray();try{await AppHost.PurchaseRequisitions!.SubmitAsync(_companyId,_documentId.Value,steps,CurrentUserId);_status.Text=T("PurchaseRequisition.Submitted");await RefreshWorkflowAsync();}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async Task DecideAsync(bool approve){if(_documentId is null||_approvals.SelectedItem is not ApprovalVm row){MessageBox.Show(this,T("PurchaseRequisition.NoDocument"),Title);return;}if(!AppHost.Permissions.Has("PUR.REQ.APPROVE")){MessageBox.Show(this,T("PurchaseRequisition.PermissionDenied"),Title);return;}try{await AppHost.PurchaseRequisitions!.DecideAsync(_companyId,_documentId.Value,row.Id,CurrentUserId,approve,_reason.Text);await RefreshWorkflowAsync();_status.Text=T("PurchaseRequisition.DecisionSaved");}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async Task ConvertAsync(){if(_documentId is null||!long.TryParse(_supplierId.Text,out var supplierId)||supplierId<=0||string.IsNullOrWhiteSpace(_supplierName.Text)){MessageBox.Show(this,T("PurchaseRequisition.InvalidInput"),Title);return;}if(!AppHost.Permissions.Has("PUR.REQ.CONVERT")){MessageBox.Show(this,T("PurchaseRequisition.PermissionDenied"),Title);return;}try{var result=await AppHost.PurchaseRequisitions!.ConvertToPurchaseOrderDraftAsync(_companyId,_documentId.Value,supplierId,_supplierName.Text,_currency.Text,decimal.Parse(_rate.Text,CultureInfo.InvariantCulture),ParseLong(_warehouse.Text),CurrentUserId);_status.Text=$"{T("PurchaseRequisition.Converted")} {result.PurchaseOrderNo}";await RefreshWorkflowAsync();}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async Task RefreshWorkflowAsync(){_approvalRows.Clear();if(_documentId is null)return;var w=await AppHost.PurchaseRequisitions!.GetAsync(_companyId,_documentId.Value);if(w is null){_status.Text="Draft";return;}_status.Text=T("PurchaseRequisition.Status")+": "+w.Status;foreach(var a in w.Approvals)_approvalRows.Add(new(a.ApprovalId,a.StepNo,a.ApproverUserId,a.Status,a.Reason??""));}
    private static long? ParseLong(string? s)=>long.TryParse(s,NumberStyles.Integer,CultureInfo.InvariantCulture,out var v)?v:null;
    private static decimal ParseDecimal(string s)=>decimal.TryParse(s,NumberStyles.Number,CultureInfo.InvariantCulture,out var v)?v:0m;
    private sealed class LineVm{public int LineNo{get;set;}public string MaterialId{get;set;}="";public string MaterialCode{get;set;}="";public string Description{get;set;}="";public string Unit{get;set;}="";public string Quantity{get;set;}="1";public string EstimatedPrice{get;set;}="0";}
    private sealed record ApprovalVm(long Id,int StepNo,string Approver,string Status,string Reason);
}
