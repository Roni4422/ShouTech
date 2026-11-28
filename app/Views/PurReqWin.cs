using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

/// <summary>Internal purchase request editor, sequential approvals, budget-checked
/// submission, cancel/close lifecycle, and conversion to a linked purchase-order
/// or request-for-quotation draft.</summary>
public sealed class PurchaseRequisitionWin : BaseWin
{
    protected override string? ScheduleTintName => "Pollen";
    private Guid _companyId;
    private long? _documentId;
    private readonly TextBox _number=new(),_department=new(),_notes=new(),_approvers=new(),_reason=new(),_supplierId=new(),_supplierName=new(),_currency=new(){Text="USD"},_rate=new(){Text="1"},_warehouse=new();
    private readonly DatePicker _date=new(){SelectedDate=DateTime.Today};
    private readonly DatePicker _requiredBy=new();
    private readonly ComboBox _requestType=new();
    private readonly DataGrid _lines=new(),_approvals=new();
    private readonly ObservableCollection<LineVm> _lineRows=new();
    private readonly ObservableCollection<ApprovalVm> _approvalRows=new();
    private readonly TextBlock _status=new();
    private readonly Button _new=new(),_load=new(),_save=new(),_submit=new(),_approve=new(),_reject=new(),_convert=new(),_convertRfq=new(),_cancelReq=new(),_closeReq=new(),_attachments=new(),_close=new();

    public PurchaseRequisitionWin()
    {
        // Screen-fit cap (step 12 rule): the minimum stays under 684x384;
        // BaseWin clamps the opening size to the work area and the body
        // scrolls at the minimum instead of clipping.
        Title=T("PurchaseRequisition.Title");Width=1024;Height=680;MinWidth=660;MinHeight=372;
        _lines.ItemsSource=_lineRows;_approvals.ItemsSource=_approvalRows;BuildUi();Loaded+=async(_,_)=>await InitializeAsync();
    }
    private async Task InitializeAsync()
    {
        if(AppHost.PurchaseRequisitions is null||AppHost.Orders is null||AppHost.JournalStore is null){MessageBox.Show(this,T("PurchaseRequisition.ServiceUnavailable"),Title);Close();return;}
        if(!AppHost.Permissions.Has("PUR.REQ.VIEW")&&!AppHost.Permissions.Has("PUR.REQ.EDIT")&&!AppHost.Permissions.Has("PUR.REQ.APPROVE")&&!AppHost.Permissions.Has("PUR.REQ.CONVERT")&&!AppHost.Permissions.Has("PUR.REQ.CANCEL")&&!AppHost.Permissions.Has("PUR.REQ.CLOSE")){MessageBox.Show(this,T("PurchaseRequisition.PermissionDenied"),Title);Close();return;}
        try{_companyId=await AppHost.JournalStore.ResolveCompanyIdAsync(CurrentCompanyCode);if(_companyId==Guid.Empty)throw new InvalidOperationException("Company context is unavailable.");await NewAsync();}
        catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);Close();}
    }
    private void BuildUi()
    {
        var root=new DockPanel{Margin=new Thickness(12)};
        // Action buttons live in the bottom bar (standing layout rule),
        // with the status line directly above them.
        var bottomBar=new StackPanel();
        var statusBar=new Border{Child=_status,Padding=new Thickness(5),BorderThickness=new Thickness(0,1,0,0),Margin=new Thickness(0,8,0,0)};
        var toolbar=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,4,0,0)};
        StyleButton(_new,"PurchaseRequisition.New");StyleButton(_load,"PurchaseRequisition.Load");StyleButton(_save,"PurchaseRequisition.Save");StyleButton(_submit,"PurchaseRequisition.Submit");StyleButton(_approve,"PurchaseRequisition.Approve");StyleButton(_reject,"PurchaseRequisition.Reject");StyleButton(_convert,"PurchaseRequisition.Convert");StyleButton(_convertRfq,"PurchaseRequisition.ConvertToRfq");StyleButton(_cancelReq,"PurchaseRequisition.CancelRequisition");StyleButton(_closeReq,"PurchaseRequisition.CloseRequisition");StyleButton(_attachments,"PurchaseAttachments.Open");StyleButton(_close,"PurchaseRequisition.Close");
        _new.Click+=async(_,_)=>await NewAsync();_load.Click+=async(_,_)=>await LoadAsync();_save.Click+=async(_,_)=>await SaveAsync();_submit.Click+=async(_,_)=>await SubmitAsync();_approve.Click+=async(_,_)=>await DecideAsync(true);_reject.Click+=async(_,_)=>await DecideAsync(false);_convert.Click+=async(_,_)=>await ConvertAsync();_convertRfq.Click+=async(_,_)=>await ConvertToRfqAsync();_cancelReq.Click+=async(_,_)=>await CancelRequisitionAsync();_closeReq.Click+=async(_,_)=>await CloseRequisitionAsync();_attachments.Click+=(_,_)=>OpenAttachments();_close.Click+=(_,_)=>Close();
        foreach(var b in new[]{_new,_load,_save,_submit,_approve,_reject,_convert,_convertRfq,_cancelReq,_closeReq,_attachments,_close})toolbar.Children.Add(b);
        bottomBar.Children.Add(statusBar);bottomBar.Children.Add(toolbar);DockPanel.SetDock(bottomBar,Dock.Bottom);root.Children.Add(bottomBar);
        var body=new Grid();body.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});body.RowDefinitions.Add(new RowDefinition{Height=new GridLength(2,GridUnitType.Star)});body.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});body.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        var header=new Grid{Margin=new Thickness(0,0,0,8)};for(int i=0;i<4;i++)header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});for(int i=0;i<3;i++)header.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        AddField(header,0,0,"PurchaseRequisition.Number",_number);AddField(header,1,0,"PurchaseRequisition.Date",_date);AddField(header,2,0,"PurchaseRequisition.Department",_department);AddField(header,3,0,"PurchaseRequisition.Currency",_currency);AddField(header,0,1,"PurchaseRequisition.ExchangeRate",_rate);AddField(header,1,1,"PurchaseRequisition.Warehouse",_warehouse);AddField(header,2,1,"PurchaseRequisition.Notes",_notes);AddField(header,3,1,"PurchaseRequisition.Approvers",_approvers);AddField(header,0,2,"PurchaseRequisition.RequiredDate",_requiredBy);AddField(header,1,2,"PurchaseRequisition.RequestType",_requestType);Grid.SetRow(header,0);body.Children.Add(header);
        ConfigureGrid(_lines);AddCol(_lines,"PurchaseRequisition.LineNo",nameof(LineVm.LineNo),55);AddCol(_lines,"PurchaseRequisition.MaterialId",nameof(LineVm.MaterialId),85);AddCol(_lines,"PurchaseRequisition.MaterialCode",nameof(LineVm.MaterialCode),100);AddCol(_lines,"PurchaseRequisition.Description",nameof(LineVm.Description),2);AddCol(_lines,"PurchaseRequisition.Unit",nameof(LineVm.Unit),65);AddCol(_lines,"PurchaseRequisition.Quantity",nameof(LineVm.Quantity),85);AddCol(_lines,"PurchaseRequisition.EstimatedPrice",nameof(LineVm.EstimatedPrice),110);Grid.SetRow(_lines,1);body.Children.Add(_lines);
        var decision=new Grid{Margin=new Thickness(0,8,0,8)};decision.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(2,GridUnitType.Star)});decision.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});decision.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});AddField(decision,0,0,"PurchaseRequisition.Reason",_reason);AddField(decision,1,0,"PurchaseRequisition.SupplierId",_supplierId);AddField(decision,2,0,"PurchaseRequisition.SupplierName",_supplierName);Grid.SetRow(decision,2);body.Children.Add(decision);
        ConfigureGrid(_approvals);AddCol(_approvals,"PurchaseRequisition.Round",nameof(ApprovalVm.RoundNo),55);AddCol(_approvals,"PurchaseRequisition.ApprovalStep",nameof(ApprovalVm.StepNo),65);AddCol(_approvals,"PurchaseRequisition.Approver",nameof(ApprovalVm.Approver),150);AddCol(_approvals,"PurchaseRequisition.ApprovalStatus",nameof(ApprovalVm.Status),100);AddCol(_approvals,"PurchaseRequisition.ApprovalReason",nameof(ApprovalVm.Reason),2);Grid.SetRow(_approvals,3);body.Children.Add(_approvals);
        // Scroll guard: at the 660x372 minimum the stacked bands scroll
        // vertically instead of clipping; the bottom action bar and the
        // status line stay visible regardless.
        var bodyScroll=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        root.Children.Add(bodyScroll);Content=root;ApplyLocalizedText();
    }
    private static void RelocalizeLabels(DependencyObject parent)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if(child is TextBlock label && label.Tag is string key) label.Text=T(key);
            RelocalizeLabels(child);
        }
    }
    private static void StyleButton(Button b,string key){b.Tag=key;b.Content=T(key);b.MinWidth=82;b.Margin=new Thickness(3);b.Padding=new Thickness(8,4,8,4);}
    private static void ConfigureGrid(DataGrid g){g.AutoGenerateColumns=false;g.CanUserAddRows=true;g.CanUserDeleteRows=true;g.HeadersVisibility=DataGridHeadersVisibility.Column;g.Margin=new Thickness(0,0,0,8);}
    private static void AddCol(DataGrid g,string key,string path,double width){g.Columns.Add(new DataGridTextColumn{Header=T(key),Binding=new Binding(path){UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=width==2?new DataGridLength(1,DataGridLengthUnitType.Star):new DataGridLength(width)});}
    private static void AddField(Grid g,int col,int row,string key,Control input){var panel=new StackPanel{Margin=new Thickness(4)};panel.Children.Add(new TextBlock{Text=T(key),Tag=key,Margin=new Thickness(0,0,0,3)});input.Margin=new Thickness(0,0,4,0);input.MinWidth=100;panel.Children.Add(input);Grid.SetColumn(panel,col);Grid.SetRow(panel,row);g.Children.Add(panel);}

    protected override void ApplyLocalizedText()
    {
        Title = T("PurchaseRequisition.Title");
        StyleButton(_new, "PurchaseRequisition.New");
        StyleButton(_load, "PurchaseRequisition.Load");
        StyleButton(_save, "PurchaseRequisition.Save");
        StyleButton(_submit, "PurchaseRequisition.Submit");
        StyleButton(_approve, "PurchaseRequisition.Approve");
        StyleButton(_reject, "PurchaseRequisition.Reject");
        StyleButton(_convert, "PurchaseRequisition.Convert");
        StyleButton(_convertRfq, "PurchaseRequisition.ConvertToRfq");
        StyleButton(_cancelReq, "PurchaseRequisition.CancelRequisition");
        StyleButton(_closeReq, "PurchaseRequisition.CloseRequisition");
        StyleButton(_attachments, "PurchaseAttachments.Open");
        StyleButton(_close, "PurchaseRequisition.Close");
        FillRequestTypes();
        RelocalizeLabels(this);
        if (_lines.Columns.Count == 7)
        {
            var keys = new[] { "PurchaseRequisition.LineNo", "PurchaseRequisition.MaterialId", "PurchaseRequisition.MaterialCode", "PurchaseRequisition.Description", "PurchaseRequisition.Unit", "PurchaseRequisition.Quantity", "PurchaseRequisition.EstimatedPrice" };
            for (var i = 0; i < keys.Length; i++) _lines.Columns[i].Header = T(keys[i]);
        }
        if (_approvals.Columns.Count == 5)
        {
            var keys = new[] { "PurchaseRequisition.Round", "PurchaseRequisition.ApprovalStep", "PurchaseRequisition.Approver", "PurchaseRequisition.ApprovalStatus", "PurchaseRequisition.ApprovalReason" };
            for (var i = 0; i < keys.Length; i++) _approvals.Columns[i].Header = T(keys[i]);
        }
    }

    private void FillRequestTypes()
    {
        var selected = SelectedRequestType();
        _requestType.Items.Clear();
        foreach (var type in PurchaseRequisitionRequestTypes.All)
            _requestType.Items.Add(new ComboBoxItem { Tag = type, Content = T("PurchaseRequisition.RequestType." + type) });
        SelectRequestType(selected);
    }
    private string SelectedRequestType()
        => _requestType.SelectedItem is ComboBoxItem item && item.Tag is string value
            ? value
            : PurchaseRequisitionRequestTypes.Purchase;
    private void SelectRequestType(string? value)
    {
        var normalized = PurchaseRequisitionRequestTypes.Normalize(value);
        foreach (var obj in _requestType.Items)
            if (obj is ComboBoxItem item && item.Tag is string tag && tag == normalized)
            {
                _requestType.SelectedItem = item;
                return;
            }
        _requestType.SelectedIndex = 0;
    }

    private async Task NewAsync(){_documentId=null;_attachments.IsEnabled=false;_number.Text=await AppHost.Orders!.NextDocNoAsync(CommercialDocSide.Purchase,_companyId,CommercialDocKinds.PurchaseRequisition);_date.SelectedDate=DateTime.Today;_requiredBy.SelectedDate=null;SelectRequestType(PurchaseRequisitionRequestTypes.Purchase);_department.Clear();_warehouse.Clear();_notes.Clear();_approvers.Clear();_reason.Clear();_supplierId.Clear();_supplierName.Clear();_lineRows.Clear();_lineRows.Add(new(){LineNo=1,Quantity="1",EstimatedPrice="0"});_approvalRows.Clear();_status.Text="Draft";}
    private CommercialDocumentDraft BuildDraft(){var currency=_currency.Text.Trim().ToUpperInvariant();if(!decimal.TryParse(_rate.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out var rate)||rate<=0)throw new InvalidOperationException(T("PurchaseRequisition.InvalidInput"));var lines=_lineRows.Where(x=>!string.IsNullOrWhiteSpace(x.Description)).Select((x,i)=>new CommercialLineDraft(ParseLong(x.MaterialId),x.MaterialCode,x.Description,x.Unit,ParseDecimal(x.Quantity),ParseDecimal(x.EstimatedPrice),0,0,ParseLong(_warehouse.Text),i+1)).ToArray();if(lines.Length==0)throw new InvalidOperationException(T("PurchaseRequisition.InvalidInput"));DateOnly? requiredBy=_requiredBy.SelectedDate is { } day?DateOnly.FromDateTime(day):null;var department=string.IsNullOrWhiteSpace(_department.Text)?null:_department.Text.Trim();return new(_companyId,CommercialDocKinds.PurchaseRequisition,_number.Text.Trim(),DateOnly.FromDateTime(_date.SelectedDate??DateTime.Today),null,null,ParseLong(_warehouse.Text),currency,rate,_notes.Text,lines,DeliveryDate:requiredBy,RequiredByDate:requiredBy,RequestType:SelectedRequestType(),Department:department);}
    private async Task SaveAsync(){if(!AppHost.Permissions.Has("PUR.REQ.EDIT")){MessageBox.Show(this,T("PurchaseRequisition.PermissionDenied"),Title);return;}try{var draft=BuildDraft();if(_documentId is null){_documentId=await AppHost.PurchaseRequisitions!.CreateDraftAsync(draft,CurrentUserId);var saved=await AppHost.Orders!.FindByIdAsync(CommercialDocSide.Purchase,_documentId.Value);if(saved is not null)_number.Text=saved.DocNo;}else await AppHost.PurchaseRequisitions!.UpdateDraftAsync(_companyId,_documentId.Value,draft,CurrentUserId);_status.Text=T("PurchaseRequisition.Saved");_attachments.IsEnabled=_documentId is not null;}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async Task LoadAsync(){try{var doc=await AppHost.Orders!.FindByNumberAsync(CommercialDocSide.Purchase,_companyId,CommercialDocKinds.PurchaseRequisition,_number.Text.Trim());if(doc is null)throw new InvalidOperationException(T("PurchaseRequisition.NoDocument"));_documentId=doc.DocId;_attachments.IsEnabled=true;_number.Text=doc.DocNo;_date.SelectedDate=doc.DocDate.ToDateTime(TimeOnly.MinValue);_notes.Text=doc.Notes??"";_department.Text=doc.Department??"";_warehouse.Text=doc.WarehouseId?.ToString(CultureInfo.InvariantCulture)??"";_requiredBy.SelectedDate=doc.RequiredByDate?.ToDateTime(TimeOnly.MinValue);SelectRequestType(doc.RequestType);_currency.Text=doc.CurrencyCode;_rate.Text=doc.ExchangeRate.ToString(CultureInfo.InvariantCulture);_lineRows.Clear();foreach(var x in doc.Lines)_lineRows.Add(new(){LineNo=x.LineNo,MaterialId=x.MaterialId?.ToString(CultureInfo.InvariantCulture)??"",MaterialCode=x.MaterialCode??"",Description=x.Description,Unit=x.Unit??"",Quantity=x.Qty.ToString(CultureInfo.InvariantCulture),EstimatedPrice=x.UnitPrice.ToString(CultureInfo.InvariantCulture)});await RefreshWorkflowAsync();}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private void OpenAttachments()
    {
        if (_documentId is not long documentId || _companyId == Guid.Empty)
        {
            MessageBox.Show(this, T("OrderDoc.SaveFirst"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (AppHost.PurchaseAttachments is null)
        {
            MessageBox.Show(this, T("PurchaseAttachments.MissingService"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        new PurchaseAttachmentsWin(_companyId, documentId, CurrentUserId) { Owner = this }.ShowDialog();
    }
    private async Task SubmitAsync(){if(_documentId is null){await SaveAsync();if(_documentId is null)return;}if(!AppHost.Permissions.Has("PUR.REQ.EDIT"))return;var users=_approvers.Text.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);var steps=users.Select((u,i)=>new PurchaseRequisitionApprovalStep(i+1,u)).ToArray();try{await AppHost.PurchaseRequisitions!.SubmitAsync(_companyId,_documentId.Value,steps,CurrentUserId);_status.Text=T("PurchaseRequisition.Submitted");await RefreshWorkflowAsync();}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async Task DecideAsync(bool approve){if(_documentId is null||_approvals.SelectedItem is not ApprovalVm row){MessageBox.Show(this,T("PurchaseRequisition.NoDocument"),Title);return;}if(!AppHost.Permissions.Has("PUR.REQ.APPROVE")){MessageBox.Show(this,T("PurchaseRequisition.PermissionDenied"),Title);return;}try{await AppHost.PurchaseRequisitions!.DecideAsync(_companyId,_documentId.Value,row.Id,CurrentUserId,approve,_reason.Text);await RefreshWorkflowAsync();_status.Text=T("PurchaseRequisition.DecisionSaved");}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async Task ConvertAsync(){if(_documentId is null||!long.TryParse(_supplierId.Text,out var supplierId)||supplierId<=0||string.IsNullOrWhiteSpace(_supplierName.Text)){MessageBox.Show(this,T("PurchaseRequisition.InvalidInput"),Title);return;}if(!AppHost.Permissions.Has("PUR.REQ.CONVERT")){MessageBox.Show(this,T("PurchaseRequisition.PermissionDenied"),Title);return;}try{var result=await AppHost.PurchaseRequisitions!.ConvertToPurchaseOrderDraftAsync(_companyId,_documentId.Value,supplierId,_supplierName.Text,_currency.Text,decimal.Parse(_rate.Text,CultureInfo.InvariantCulture),ParseLong(_warehouse.Text),CurrentUserId);_status.Text=$"{T("PurchaseRequisition.Converted")} {result.PurchaseOrderNo}";await RefreshWorkflowAsync();}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async Task ConvertToRfqAsync(){if(_documentId is null){MessageBox.Show(this,T("PurchaseRequisition.NoDocument"),Title);return;}if(!AppHost.Permissions.Has("PUR.REQ.CONVERT")){MessageBox.Show(this,T("PurchaseRequisition.PermissionDenied"),Title);return;}try{var result=await AppHost.PurchaseRequisitions!.ConvertToRequestForQuotationDraftAsync(_companyId,_documentId.Value,ParseLong(_warehouse.Text),CurrentUserId);_status.Text=$"{T("PurchaseRequisition.ConvertedToRfq")} {result.RfqNo}";await RefreshWorkflowAsync();}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async Task CancelRequisitionAsync(){if(_documentId is null){MessageBox.Show(this,T("PurchaseRequisition.NoDocument"),Title);return;}if(!AppHost.Permissions.Has("PUR.REQ.CANCEL")){MessageBox.Show(this,T("PurchaseRequisition.PermissionDenied"),Title);return;}if(string.IsNullOrWhiteSpace(_reason.Text)){MessageBox.Show(this,T("PurchaseRequisition.ReasonRequired"),Title,MessageBoxButton.OK,MessageBoxImage.Warning);return;}try{await AppHost.PurchaseRequisitions!.CancelAsync(_companyId,_documentId.Value,CurrentUserId,_reason.Text);_status.Text=T("PurchaseRequisition.Cancelled");await RefreshWorkflowAsync();}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async Task CloseRequisitionAsync(){if(_documentId is null){MessageBox.Show(this,T("PurchaseRequisition.NoDocument"),Title);return;}if(!AppHost.Permissions.Has("PUR.REQ.CLOSE")){MessageBox.Show(this,T("PurchaseRequisition.PermissionDenied"),Title);return;}try{await AppHost.PurchaseRequisitions!.CloseAsync(_companyId,_documentId.Value,CurrentUserId);_status.Text=T("PurchaseRequisition.Closed");await RefreshWorkflowAsync();}catch(Exception ex){MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async Task RefreshWorkflowAsync(){_approvalRows.Clear();if(_documentId is null)return;var w=await AppHost.PurchaseRequisitions!.GetAsync(_companyId,_documentId.Value);if(w is null){_status.Text="Draft";return;}_status.Text=T("PurchaseRequisition.Status")+": "+w.Status;foreach(var a in w.Approvals)_approvalRows.Add(new(a.ApprovalId,a.RoundNo,a.StepNo,a.ApproverUserId,a.Status,a.Reason??""));}
    private static long? ParseLong(string? s)=>long.TryParse(s,NumberStyles.Integer,CultureInfo.InvariantCulture,out var v)?v:null;
    private static decimal ParseDecimal(string s)=>decimal.TryParse(s,NumberStyles.Number,CultureInfo.InvariantCulture,out var v)?v:0m;
    private sealed class LineVm{public int LineNo{get;set;}public string MaterialId{get;set;}="";public string MaterialCode{get;set;}="";public string Description{get;set;}="";public string Unit{get;set;}="";public string Quantity{get;set;}="1";public string EstimatedPrice{get;set;}="0";}
    private sealed record ApprovalVm(long Id,int RoundNo,int StepNo,string Approver,string Status,string Reason);
}
