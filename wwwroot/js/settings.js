function showExtraFieldModal() {
    $.get(`/Home/GetExtraFieldsModal?importMode=0`, function (data) {
        $("#extraFieldModalContent").html(data);
        $("#extraFieldModal").modal('show');
    });
}
function hideExtraFieldModal() {
    $("#extraFieldModal").modal('hide');
}
function getCheckedTabs() {
    var visibleTabs = $("#visibleTabs input[type='checkbox']:checked").map(function () {
        return this.value;
    }).toArray();
    if (!visibleTabs.includes("Dashboard")) {
        visibleTabs.unshift("Dashboard");
    }
    return visibleTabs;
}
function deleteLanguage() {
    var languageFileLocation = `/translations/${$("#defaultLanguage").val()}.json`;
    $.post('/Files/DeleteFiles', { fileLocation: languageFileLocation }, function (data) {
        //reset user language back to en_US
        $("#defaultLanguage").val('en_US');
        updateSettings(true);
    });
}
function deleteTheme() {
    var themeFileLocation = `/themes/${$("#defaultTheme").val()}.css`;
    $.post('/Files/DeleteFiles', { fileLocation: themeFileLocation }, function (data) {
        //reset user theme
        $("#defaultTheme").val('');
        updateSettings(true);
    });
}
function gbSelectedColorMode() {
    return $('input[name="gbColorMode"]:checked').val() || 'light';
}
function gbColorModeChanged() {
    var mode = gbSelectedColorMode();
    var root = document.documentElement;
    root.setAttribute('data-gb-color-mode', mode);
    if (mode === 'system') {
        gbApplyColorMode();
    } else {
        root.setAttribute('data-bs-theme', mode);
    }
    updateSettings();
}
function gbCollectUserConfig() {
    var visibleTabs = getCheckedTabs();
    var defaultTab = $("#defaultTab").val();
    if (!visibleTabs.includes(defaultTab)) {
        defaultTab = "Dashboard";
    }
    var colorMode = gbSelectedColorMode();
    return {
        useDarkMode: colorMode == 'dark',
        useSystemColorMode: colorMode == 'system',
        enableCsvImports: $("#enableCsvImports").is(':checked'),
        useMPG: $("#unitsImperial").is(':checked'),
        useDescending: $("#useDescending").is(':checked'),
        hideZero: $("#hideZero").is(":checked"),
        automaticDecimalFormat: $("#automaticDecimalFormat").is(":checked"),
        useUKMpg: $("#useUKMPG").is(":checked"),
        useThreeDecimalGasCost: $("#useThreeDecimal").is(":checked"),
        useThreeDecimalGasConsumption: $("#useThreeDecimalGasConsumption").is(":checked"),
        useMarkDownOnSavedNotes: $("#useMarkDownOnSavedNotes").is(":checked"),
        enableAutoReminderRefresh: $("#enableAutoReminderRefresh").is(":checked"),
        enableAutoOdometerInsert: $("#enableAutoOdometerInsert").is(":checked"),
        enableAutoFillOdometer: $("#enableAutoFillOdometer").is(":checked"),
        enableShopSupplies: $("#enableShopSupplies").is(":checked"),
        enablePersistentDialog: $("#enablePersistentDialog").is(":checked"),
        showCalendar: $("#showCalendar").is(":checked"),
        showVehicleThumbnail: $("#showVehicleThumbnail").is(":checked"),
        showSearch: false,
        disableAutoZoom: $("#disableAutoZoom").is(":checked"),
        enableExtraFieldColumns: $("#enableExtraFieldColumns").is(":checked"),
        hideSoldVehicles: $("#hideSoldVehicles").is(":checked"),
        preferredGasUnit: $("#preferredGasUnit").val(),
        preferredGasMileageUnit: $("#preferredFuelMileageUnit").val(),
        userLanguage: $("#defaultLanguage").val(),
        userTheme: $("#defaultTheme").val(),
        useUnitForFuelCost: $("#useUnitForFuelCost").is(":checked"),
        useGridInMobile: $("#useGridInMobile").is(":checked"),
        enableNotesColumnWrap: $("#enableNotesColumnWrap").is(":checked"),
        hideCostTracking: !$("#trackCosts").is(":checked"),
        visibleTabs: visibleTabs,
        defaultTab: defaultTab
    };
}
var gbSettingsRequest = null;
function updateSettings(reloadAfterSave) {
    var userConfigObject = gbCollectUserConfig();
    //keep the default section picker honest when its section was just hidden
    if ($("#defaultTab").val() != userConfigObject.defaultTab) {
        $("#defaultTab").val(userConfigObject.defaultTab);
    }
    //costs can be hidden or shown without a reload
    $("body").toggleClass("gb-hide-costs", userConfigObject.hideCostTracking);
    if (reloadAfterSave) {
        sloader.show();
    }
    if (gbSettingsRequest && gbSettingsRequest.readyState != 4) {
        gbSettingsRequest.abort();
    }
    gbSettingsRequest = $.post('/Home/WriteToSettings', { userConfig: userConfigObject }, function (data) {
        if (data) {
            if (reloadAfterSave) {
                setTimeout(function () { window.location.href = '/Home/Index?tab=settings' }, 300);
            } else {
                gbShowSaved();
            }
        } else {
            sloader.hide();
            errorToast(genericErrorMessage());
        }
    }).fail(function (xhr, status) {
        if (status != 'abort') {
            sloader.hide();
            errorToast(genericErrorMessage());
        }
    });
}
/* ---------- notifications (ntfy) ---------- */
function gbCollectNtfy() {
    return {
        enabled: $("#ntfyEnabled").is(":checked"),
        serverUrl: $("#ntfyServerUrl").val().trim(),
        topic: $("#ntfyTopic").val().trim(),
        accessToken: $("#ntfyAccessToken").val().trim(),
        username: $("#ntfyUsername").val().trim(),
        password: $("#ntfyPassword").val(),
        appUrl: $("#ntfyAppUrl").val().trim(),
        notifyHeadsUp: $("#ntfyHeadsUp").is(":checked"),
        notifyDueSoon: $("#ntfyDueSoon").is(":checked"),
        notifyUrgent: $("#ntfyUrgent").is(":checked"),
        notifyOverdue: $("#ntfyOverdue").is(":checked"),
        headsUpDistance: parseInt($("#ntfyHeadsUpDistance").val()) || 0,
        headsUpDays: parseInt($("#ntfyHeadsUpDays").val()) || 0,
        overdueRepeatDays: parseInt($("#ntfyOverdueRepeat").val()) || 0,
        quietHoursEnabled: $("#ntfyQuiet").is(":checked"),
        quietHoursStart: parseInt($("#ntfyQuietStart").val()) || 0,
        quietHoursEnd: parseInt($("#ntfyQuietEnd").val()) || 0
    };
}
function gbValidateNtfy(ntfy, requireTopic) {
    if ((ntfy.enabled || requireTopic) && ntfy.topic == '') {
        errorToast("Enter the ntfy topic you subscribed to");
        $("#ntfyTopic").addClass('is-invalid').trigger('focus');
        return false;
    }
    $("#ntfyTopic").removeClass('is-invalid');
    return true;
}
function gbSyncNtfyForm() {
    var enabled = $("#ntfyEnabled").is(":checked");
    $("#gbNtfyForm .gb-group").not(':first').toggleClass('opacity-50', !enabled);
    $("#gbHeadsUpDefaults").toggleClass('d-none', !$("#ntfyHeadsUp").is(":checked"));
}
function gbSaveNtfy() {
    var ntfy = gbCollectNtfy();
    if (!gbValidateNtfy(ntfy, false)) {
        return;
    }
    var clearSecrets = $("#ntfyClearSecrets").is(":checked");
    sloader.show();
    $.post('/Home/SaveNotificationSettings', { ntfy: ntfy, clearSecrets: clearSecrets }, function (data) {
        sloader.hide();
        if (data.success) {
            $("#ntfyAccessToken, #ntfyPassword").val('');
            if (data.hasToken != undefined) {
                $("#ntfyAccessToken").attr('placeholder', data.hasToken ? 'Saved, leave blank to keep' : 'tk_...');
            }
            if (data.hasPassword != undefined) {
                $("#ntfyPassword").attr('placeholder', data.hasPassword ? 'Saved, leave blank to keep' : '');
            }
            if (clearSecrets) {
                $("#ntfyClearSecrets").closest('.form-check').remove();
            }
            gbShowSaved(ntfy.enabled ? "Notifications on" : "Notification settings saved");
        } else {
            errorToast(data.message || genericErrorMessage());
        }
    }).fail(function () {
        sloader.hide();
        errorToast(genericErrorMessage());
    });
}
function gbTestNtfy() {
    var ntfy = gbCollectNtfy();
    if (!gbValidateNtfy(ntfy, true)) {
        return;
    }
    sloader.show();
    $.post('/Home/SendNtfyTest', { ntfy: ntfy }, function (data) {
        sloader.hide();
        if (data.success) {
            successToast(data.message || "Test notification sent");
        } else {
            errorToast(data.message || genericErrorMessage());
        }
    }).fail(function () {
        sloader.hide();
        errorToast(genericErrorMessage());
    });
}
function gbCheckNtfyNow() {
    sloader.show();
    $.post('/Home/CheckNotificationsNow', function (data) {
        sloader.hide();
        if (data.success) {
            successToast(data.message);
        } else {
            errorToast(data.message || genericErrorMessage());
        }
    }).fail(function () {
        sloader.hide();
        errorToast(genericErrorMessage());
    });
}
function makeBackup() {
    $.get('/Files/MakeBackup', function (data) {
        window.location.href = data;
    });
}
function openUploadLanguage() {
    $("#inputLanguage").trigger('click');
}
function openRestoreBackup() {
    $("#inputBackup").trigger('click');
}
function uploadLanguage(event) {
    let formData = new FormData();
    formData.append("file", event.files[0]);
    sloader.show();
    $.ajax({
        url: "/Files/HandleTranslationFileUpload",
        data: formData,
        cache: false,
        processData: false,
        contentType: false,
        type: 'POST'
    }).done((response) => {
        sloader.hide();
        if (response.success) {
            setTimeout(function () { window.location.href = '/Home/Index?tab=settings' }, 500);
        } else {
            errorToast(response.message);
        }
    }).fail(() => {
        sloader.hide();
        errorToast("An error has occurred, please check the file size and try again later.");
    });
}
function openUploadTheme() {
    $("#inputTheme").trigger('click');
}
function uploadTheme(event) {
    let formData = new FormData();
    formData.append("file", event.files[0]);
    sloader.show();
    $.ajax({
        url: "/Files/HandleThemeFileUpload",
        data: formData,
        cache: false,
        processData: false,
        contentType: false,
        type: 'POST'
    }).done((response) => {
        sloader.hide();
        if (response.success) {
            setTimeout(function () { window.location.href = '/Home/Index?tab=settings' }, 500);
        } else {
            errorToast(response.message);
        }
    }).fail(() => {
        sloader.hide();
        errorToast("An error has occurred, please check the file size and try again later.");
    });
}
function restoreBackup(event) {
    let formData = new FormData();
    formData.append("file", event.files[0]);
    console.log('LubeLogger - DB Restoration Started');
    sloader.show();
    $.ajax({
        url: "/Files/HandleFileUpload",
        data: formData,
        cache: false,
        processData: false,
        contentType: false,
        type: 'POST'
    }).done((response) => {
        if (response.trim() != '') {
            $.post('/Files/RestoreBackup', { fileName: response }, function (data) {
                sloader.hide();
                if (data) {
                    console.log('LubeLogger - DB Restoration Completed');
                    successToast("Backup Restored");
                    setTimeout(function () { window.location.href = '/Home/Index' }, 500);
                } else {
                    errorToast(genericErrorMessage());
                    console.log('LubeLogger - DB Restoration Failed - Failed to process backup file.');
                }
            });
        } else {
            console.log('LubeLogger - DB Restoration Failed - Failed to upload backup file.');
        }
    }).fail(() => {
        sloader.hide();
        console.log('LubeLogger - DB Restoration Failed - Request failed to reach backend, please check file size.');
        errorToast("An error has occurred, please check the file size and try again later.");
    });
}

function showTranslationEditor() {
    $.get(`/Home/GetTranslatorEditor?userLanguage=${$("#defaultLanguage").val()}`, function (data) {
        $('#translationEditorModalContent').html(data);
        $('#translationEditorModal').modal('show');
    })
}
function hideTranslationEditor() {
    $('#translationEditorModal').modal('hide');
}
function createAndUploadTranslation(translationName, translationData) {
    let jsonData = JSON.stringify(translationData);
    let translationBlob = new Blob([jsonData], { type: "application/json" });
    let translationFile = new File([translationBlob], `${translationName}.json`, { type: "application/json" });
    let formData = new FormData();
    formData.append("file", translationFile);
    sloader.show();
    $.ajax({
        url: "/Home/SaveTranslation",
        data: formData,
        cache: false,
        processData: false,
        contentType: false,
        type: 'POST'
    }).done((response) => {
        sloader.hide();
        if (response.success) {
            setTimeout(function () { window.location.href = '/Home/Index?tab=settings' }, 500);
        } else {
            errorToast(response.message);
        }
    }).fail(() => {
        sloader.hide();
        errorToast("An error has occurred, please check the file size and try again later.");
    });
}
function createAndExportTranslation(translationData) {
    let jsonData = JSON.stringify(translationData);
    let translationBlob = new Blob([jsonData], { type: "application/json" });
    let translationFile = new File([translationBlob], `translationexport.json`, { type: "application/json" });
    let formData = new FormData();
    formData.append("file", translationFile);
    sloader.show();
    $.ajax({
        url: "/Home/ExportTranslation",
        data: formData,
        cache: false,
        processData: false,
        contentType: false,
        type: 'POST'
    }).done((response) => {
        sloader.hide();
        if (!response) {
            errorToast(genericErrorMessage());
        } else {
            window.location.href = response;
        }
    }).fail(() => {
        sloader.hide();
        errorToast("An error has occurred, please check the file size and try again later.");
    });
}
function saveTranslation() {
    var currentLanguage = $("#defaultLanguage").val();
    var translationData = {};
    $(".translation-keyvalue").map((index, elem) => {
        var translationKey = $(elem).find('.translation-key');
        var translationValue = $(elem).find('.translation-value textarea');
        translationData[translationKey.text().replaceAll(' ', '_').trim()] = translationValue.val().trim();
    });
    if (translationData.length == 0) {
        errorToast(genericErrorMessage());
        return;
    }
    var userCanDelete = $(".translation-delete").length > 0;
    Swal.fire({
        title: 'Save Translation',
        html: `
                                    <input type="text" id="translationFileName" class="swal2-input" placeholder="Translation Name" value="${currentLanguage}" onkeydown="handleSwalEnter(event)">
                                    `,
        confirmButtonText: 'Save',
        focusConfirm: false,
        preConfirm: () => {
            const translationFileName = $("#translationFileName").val();
            if (!translationFileName || translationFileName.trim() == '') {
                Swal.showValidationMessage(`Please enter a valid file name`);
            } else if (translationFileName.trim() == 'en_US' && !userCanDelete) {
                Swal.showValidationMessage(`en_US is reserved, please enter a different name`);
            }
            return { translationFileName }
        },
    }).then(function (result) {
        if (result.isConfirmed) {
            createAndUploadTranslation(result.value.translationFileName, translationData);
        }
    });
}
function exportTranslation(){
    var translationData = {};
    $(".translation-keyvalue").map((index, elem) => {
        var translationKey = $(elem).find('.translation-key');
        var translationValue = $(elem).find('.translation-value textarea');
        translationData[translationKey.text().replaceAll(' ', '_').trim()] = translationValue.val().trim();
    });
    if (translationData.length == 0) {
        errorToast(genericErrorMessage());
        return;
    }
    createAndExportTranslation(translationData);
}
function showTranslationDownloader() {
    $.get('/Home/GetAvailableTranslations', function(data){
        $('#translationDownloadModalContent').html(data);
        $('#translationDownloadModal').modal('show');
    })
}
function hideTranslationDownloader() {
    $('#translationDownloadModal').modal('hide');
}
function downloadTranslation(continent, name) {
    sloader.show();
    $.get(`/Home/DownloadTranslation?continent=${continent}&name=${name}`, function (data) {
        sloader.hide();
        if (data) {
            successToast("Translation downloaded");
            updateSettings(true);
        } else {
            errorToast(genericErrorMessage());
        }
    })
}
function downloadAllTranslations() {
    sloader.show();
    $.get('/Home/DownloadAllTranslations', function (data) {
        sloader.hide();
        if (data.success) {
            successToast(data.message);
            updateSettings(true);
        } else {
            errorToast(data.message);
        }
    })
}
function deleteTranslationKey(e) {
    $(e).parent().parent().remove();
}
function hideCustomWidgets() {
    $("#customWidgetModal").modal('hide');
}
function saveCustomWidgets() {
    $.post('/Home/SaveCustomWidgets', { widgetsData: $("#widgetEditor").val() }, function (data) {
        if (data) {
            successToast("Custom widgets saved");
            hideCustomWidgets();
        } else {
            errorToast(genericErrorMessage());
        }
    })
}
function deleteCustomWidgets() {
    $.post('/Home/DeleteCustomWidgets', function (data) {
        if (data) {
            successToast("Custom widgets deleted");
            hideCustomWidgets();
        } else {
            errorToast(genericErrorMessage());
        }
    })
}
function saveCustomWidgetsAcknowledgement() {
    sessionStorage.setItem('customWidgetsAcknowledged', true);
}
function getCustomWidgetsAcknowledgement() {
    let storedItem = sessionStorage.getItem('customWidgetsAcknowledged');
    if (storedItem == null || storedItem == undefined) {
        return false;
    } else {
        return storedItem;
    }
}
function showCustomWidgets() {
    let acknowledged = getCustomWidgetsAcknowledgement();
    if (acknowledged) {
        $.get('/Home/GetCustomWidgetEditor', function (data) {
            if (data.trim() != '') {
                $("#customWidgetModalContent").html(data);
                $("#customWidgetModal").modal('show');
            } else {
                errorToast("Custom Widgets Not Enabled");
            }
        });
        return;
    }
    Swal.fire({
        title: 'Warning',
        icon: "warning",
        html: `
               <span>
               You are about to use the Custom Widgets Editor, this is a developer-focused feature that can lead to security vulnerabilities if you don't understand what you're doing.
               <br />Zero support will be provided from the developer(s) of LubeLogger regarding Custom Widgets, Read the Documentation.
               <br />By proceeding, you acknowledge that you are solely responsible for all consequences from utilizing the Custom Widgets Editor.
               <br />To proceed, enter 'acknowledge' into the text field below.
               </span>
               <input type="text" id="inputAcknowledge" class="swal2-input" placeholder="acknowledge" onkeydown="handleSwalEnter(event)">
              `,
        confirmButtonText: 'Proceed',
        focusConfirm: false,
        preConfirm: () => {
            const userAcknowledge = $("#inputAcknowledge").val();
            if (!userAcknowledge || userAcknowledge != 'acknowledge') {
                Swal.showValidationMessage(`Please acknowledge before proceeding.`)
            }
            return { userAcknowledge }
        },
    }).then(function (result) {
        if (result.isConfirmed) {
            saveCustomWidgetsAcknowledgement();
            $.get('/Home/GetCustomWidgetEditor', function (data) {
                if (data.trim() != '') {
                    $("#customWidgetModalContent").html(data);
                    $("#customWidgetModal").modal('show');
                } else {
                    errorToast("Custom Widgets Not Enabled");
                }
            });
        }
    });
}
function loadTooltips() {
    $('.settingsToolTip').map((index, elem) => {
        new bootstrap.Tooltip(elem);
    })
    $('.settingsToolTip').on('click', (event) => {
        event.stopPropagation();
        event.preventDefault();
    })
}
function handleTranslationSearchKeyPress(event) {
    if (event.keyCode == 13) {
        searchTranslations();
    } else {
        setDebounce(searchTranslations);
    }
}
function searchTranslations() {
    let searchTerm = $('#translationSearchInput').val();
    if (searchTerm.trim() == '') {
        $('.lubelog-translation-row').show();
    } else {
        $('.lubelog-translation-row').hide();
        $(`.lubelog-translation-row span:containsNC('${searchTerm}')`).closest('.lubelog-translation-row').show();
    }
}
function loadSettingsPage(pageName) {
    var nav = $('.gb-settings-nav [data-settings-page]');
    nav.removeClass('active').removeAttr('aria-current');
    nav.filter(`[data-settings-page="${pageName}"]`).addClass('active').attr('aria-current', 'page');
    $('[data-settings-section]').removeClass('active');
    $(`[data-settings-section="${pageName}"]`).addClass('active');
    sessionStorage.setItem('settingsSubTab', pageName);
}
function renderSettingsSubTab() {
    $('.gb-settings-nav [data-settings-page]').off('click').on('click', function () {
        loadSettingsPage($(this).attr('data-settings-page'));
        var nav = $('.gb-settings-nav')[0];
        if (nav && getComputedStyle(nav).flexDirection == 'row') {
            this.scrollIntoView({ block: 'nearest', inline: 'center', behavior: 'smooth' });
            window.scrollTo({ top: 0, behavior: 'auto' });
        }
    });
    let storedSubTab = sessionStorage.getItem('settingsSubTab');
    if (storedSubTab == null || storedSubTab == undefined || $(`[data-settings-section="${storedSubTab}"]`).length == 0) {
        storedSubTab = 'appearance';
    }
    loadSettingsPage(storedSubTab);
    $('#ntfyEnabled, #ntfyHeadsUp').off('change.gb').on('change.gb', gbSyncNtfyForm);
    if ($("#ntfyAppUrl").length && $("#ntfyAppUrl").val().trim() == '') {
        $("#ntfyAppUrl").attr('placeholder', window.location.origin);
    }
    gbSyncNtfyForm();
}
