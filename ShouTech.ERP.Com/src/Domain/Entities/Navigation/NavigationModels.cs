using System;

namespace ShouTech.Domain.Entities.Navigation;

public class MainMenuItem
{
    public string ItemCode { get; set; } = string.Empty;
    public string TitleAr { get; set; } = string.Empty;
    public string TitleEn { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string TargetView { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}

public class RibbonLayoutRow
{
    public string TabCode { get; set; } = string.Empty;
    public string TabLocalizationKey { get; set; } = string.Empty;
    public string HeaderIcon { get; set; } = string.Empty;
    public string TabModuleCode { get; set; } = string.Empty;
    public int TabDisplayOrder { get; set; }
    public string GroupCode { get; set; } = string.Empty;
    public string GroupLocalizationKey { get; set; } = string.Empty;
    public string GroupTitleAr { get; set; } = string.Empty;
    public string GroupTitleEn { get; set; } = string.Empty;
    public int GroupDisplayOrder { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string ActionKey { get; set; } = string.Empty;
    public string ItemTitleAr { get; set; } = string.Empty;
    public string ItemTitleEn { get; set; } = string.Empty;
    public string HandlerType { get; set; } = string.Empty;
    public string TargetViewName { get; set; } = string.Empty;
    public string TargetViewModelName { get; set; } = string.Empty;
    public int ItemDisplayOrder { get; set; }
    public string PermissionCode { get; set; } = string.Empty;
}