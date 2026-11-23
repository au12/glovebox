/* CANary section of the vehicle page: the weekly report, a drive's charts, and the settings dialog.
   The report itself is rendered by the server; this file loads it and draws the charts. */

var canaryState = {
    week: '',
    tripId: '',
    picked: ['speed', 'rpm', 'boost', 'coolant_temp', 'fuel_trim_b1', 'vbat'],
    signals: [],
    series: [],
    duration: 1
};

var CANARY_GROUPS = [['engine', 'Engine'], ['boost', 'Boost'], ['fuel', 'Fuel'], ['ignition', 'Ignition'], ['valve timing', 'Valve timing'],
    ['temperatures', 'Temperatures'], ['lubrication', 'Lubrication'], ['electrical', 'Electrical'], ['status', 'Status']];

function canaryVehicle() {
    return GetVehicleId().vehicleId;
}

/* ───── report ───── */

function getCanaryReport(vehicleId, week) {
    canaryState.week = week || '';
    $.get('/Canary/Report', { vehicleId: vehicleId, week: canaryState.week }, function (data) {
        if (data) {
            $('#canary-tab-pane').html(data);
            canaryState.week = $('#canaryRoot').attr('data-week') || canaryState.week;
        }
    });
}

function canaryLoad(week) {
    if (week === undefined || week === null) {
        week = canaryState.week;
    }
    getCanaryReport(canaryVehicle(), week);
}

/* The count beside the section's name: how many things the latest week flagged. */
function canaryBadge() {
    $.get('/Canary/Badge', { vehicleId: canaryVehicle() }, function (data) {
        var badge = $('#gbCanaryCount');
        if (!data || !data.connected || !data.count) {
            badge.addClass('d-none');
            return;
        }
        badge.text(data.count).removeClass('d-none cn-count-crit cn-count-warn cn-count-adv')
            .addClass(data.worst === 'critical' ? 'cn-count-crit' : data.worst === 'warning' ? 'cn-count-warn' : 'cn-count-adv');
    });
}

function canaryMute(checkId, mute) {
    $.post('/Canary/Mute', { vehicleId: canaryVehicle(), checkId: checkId, mute: mute }, function (data) {
        if (data.success) {
            successToast(data.message);
            canaryLoad();
            canaryBadge();
        } else {
            errorToast(data.message);
        }
    });
}

function canaryCopy(elementId, button) {
    var text = $('#' + elementId).text().trim();
    var done = function () {
        var label = $(button).find('.gb-btn-label');
        var before = label.text();
        label.text('Copied');
        setTimeout(function () { label.text(before); }, 1600);
    };
    if (navigator.clipboard && window.isSecureContext) {
        navigator.clipboard.writeText(text).then(done);
    } else {
        /* plain http on a home network: the clipboard API is not available there */
        var area = $('<textarea>').val(text).css({ position: 'fixed', opacity: 0 }).appendTo('body');
        area[0].select();
        try { document.execCommand('copy'); done(); } catch (e) { /* the value is on screen to copy by hand */ }
        area.remove();
    }
}

function canaryCreateKey() {
    $.post('/Canary/CreateKey', { vehicleId: canaryVehicle() }, function (data) {
        if (data.success && data.additionalData) {
            $('[id="cnKeyRow"]').html('<code id="cnApiKey"></code>'
                + '<button type="button" class="btn btn-outline-secondary btn-sm" onclick="canaryCopy(\'cnApiKey\', this)"><i class="bi bi-clipboard"></i><span class="gb-btn-label">Copy</span></button>'
                + '<small>Copy it now: it is not shown again. It is listed with your other API keys in Settings, where it can be removed.</small>');
            $('[id="cnApiKey"]').text(data.additionalData.apiKey);
        } else {
            errorToast(data.message);
        }
    });
}

/* ───── settings ───── */

function canarySettings() {
    $.get('/Canary/Settings', { vehicleId: canaryVehicle() }, function (data) {
        if (data) {
            $('#canarySettingsModalContent').html(data);
            $('#canarySettingsModal').modal('show');
        }
    });
}

function canarySaveSettings() {
    $.post('/Canary/SaveSettings', {
        vehicleId: canaryVehicle(),
        recordOdometer: $('#cnRecordOdometer').is(':checked'),
        odometerUnit: $('#cnOdometerUnit').val(),
        odometerOffset: parseInt($('#cnOdometerOffset').val(), 10) || 0,
        weekStartsSunday: $('#cnWeekStart').val() === 'true',
        rawRetentionMonths: parseInt($('#cnRetention').val(), 10) || 0,
        notify: $('#cnNotify').is(':checked'),
        weeklyDigest: $('#cnDigest').is(':checked')
    }, function (data) {
        if (data.success) {
            successToast(data.message);
            $('#canarySettingsModal').modal('hide');
            canaryLoad('');
        } else {
            errorToast(data.message);
        }
    });
}

function canaryReanalyze() {
    $.post('/Canary/Reanalyze', { vehicleId: canaryVehicle() }, function (data) {
        if (data.success) {
            successToast(data.message);
            $('#canarySettingsModal').modal('hide');
            canaryLoad();
            canaryBadge();
        } else {
            errorToast(data.message);
        }
    });
}

function canaryForget() {
    Swal.fire({
        title: 'Remove all CANary data?',
        text: 'Every uploaded drive and report for this vehicle is deleted. Odometer records stay. A device that is still connected starts over with its next check-in.',
        showCancelButton: true,
        confirmButtonText: 'Remove',
        confirmButtonColor: '#dc3545'
    }).then(function (result) {
        if (!result.isConfirmed) {
            return;
        }
        $.post('/Canary/Forget', { vehicleId: canaryVehicle() }, function (data) {
            if (data.success) {
                successToast(data.message);
                $('#canarySettingsModal').modal('hide');
                canaryLoad('');
                canaryBadge();
            } else {
                errorToast(data.message);
            }
        });
    });
}

/* ───── one drive ───── */

function canaryTrip(tripId) {
    canaryState.tripId = tripId;
    $.get('/Canary/Trip', { vehicleId: canaryVehicle(), tripId: tripId }, function (data) {
        if (!data) {
            return;
        }
        $('#canaryTripModalContent').html(data);
        var modal = $('#canaryTripModal');
        modal.off('shown.bs.modal').on('shown.bs.modal', canaryTripReady);
        modal.modal('show');
    });
}

function canaryDeleteTrip(tripId) {
    Swal.fire({
        title: 'Delete this drive?',
        text: 'Its raw data and analysis are removed from Glovebox. The weekly report is worked out again without it.',
        showCancelButton: true,
        confirmButtonText: 'Delete',
        confirmButtonColor: '#dc3545'
    }).then(function (result) {
        if (!result.isConfirmed) {
            return;
        }
        $.post('/Canary/DeleteTrip', { vehicleId: canaryVehicle(), tripId: tripId }, function (data) {
            if (data.success) {
                successToast(data.message);
                $('#canaryTripModal').modal('hide');
                canaryLoad();
                canaryBadge();
            } else {
                errorToast(data.message);
            }
        });
    });
}

function canaryTripReady() {
    var holder = document.getElementById('cnTripSignals');
    if (!holder) {
        return;
    }
    try {
        canaryState.signals = JSON.parse(holder.textContent);
    } catch (e) {
        canaryState.signals = [];
    }
    canaryChips();
    canaryLoadSeries();
}

function canaryEscape(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
        return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
}

/* The readings on the charts, each removable, and a list to add another from. */
function canaryChips() {
    var box = document.getElementById('cnChips');
    if (!box) {
        return;
    }
    var have = canaryState.signals;
    var shown = have.filter(function (s) { return canaryState.picked.indexOf(s.id) >= 0; });
    var rest = have.filter(function (s) { return canaryState.picked.indexOf(s.id) < 0; });
    var labels = {};
    CANARY_GROUPS.forEach(function (g) { labels[g[0]] = g[1]; });
    var groups = {};
    rest.forEach(function (s) { (groups[s.group || 'other'] = groups[s.group || 'other'] || []).push(s); });
    var options = Object.keys(groups).map(function (g) {
        return '<optgroup label="' + canaryEscape(labels[g] || 'Other') + '">' + groups[g].map(function (s) {
            return '<option value="' + canaryEscape(s.id) + '">' + canaryEscape(s.name) + '</option>';
        }).join('') + '</optgroup>';
    }).join('');
    box.innerHTML = shown.map(function (s) {
        return '<button type="button" class="cn-chip" data-sig="' + canaryEscape(s.id) + '" aria-label="Remove ' + canaryEscape(s.name) + ' from the charts">'
            + canaryEscape(s.name) + '<span aria-hidden="true">×</span></button>';
    }).join('') + (rest.length ? '<select class="cn-chip cn-add" id="cnAdd" aria-label="Add a reading to the charts"><option value="">Add a reading</option>' + options + '</select>' : '');
    box.onclick = function (e) {
        var chip = e.target.closest('button.cn-chip');
        if (!chip) {
            return;
        }
        canaryState.picked = canaryState.picked.filter(function (id) { return id !== chip.dataset.sig; });
        canaryChips();
        canaryLoadSeries();
    };
    var add = document.getElementById('cnAdd');
    if (add) {
        add.onchange = function () {
            if (!add.value) {
                return;
            }
            canaryState.picked.push(add.value);
            canaryChips();
            canaryLoadSeries();
        };
    }
}

function canaryLoadSeries() {
    var box = document.getElementById('cnCharts');
    if (!box) {
        return;
    }
    var want = canaryState.signals.filter(function (s) { return canaryState.picked.indexOf(s.id) >= 0; }).map(function (s) { return s.id; });
    if (!want.length) {
        box.innerHTML = '<p class="cn-note" style="padding:14px 18px;margin:0">Pick a reading above to chart it.</p>';
        return;
    }
    var tripId = canaryState.tripId;
    var points = Math.max(150, Math.min(900, Math.round(box.clientWidth / 1.4)));
    $.get('/Canary/Series', { vehicleId: canaryVehicle(), tripId: tripId, signals: want.join(','), points: points }, function (data) {
        if (tripId !== canaryState.tripId || !document.getElementById('cnCharts')) {
            return;
        }
        if (!data || !data.ok) {
            box.innerHTML = '<p class="cn-note" style="padding:14px 18px;margin:0">' + canaryEscape((data && data.error) || 'The charts could not be loaded.') + '</p>';
            return;
        }
        canaryState.duration = Math.max(data.duration || 1, 1);
        /* keep the order the reader chose */
        canaryState.series = want.map(function (id) { return data.series.filter(function (s) { return s.id === id; })[0]; })
            .filter(function (s) { return s && s.t.length > 1; });
        canaryDraw();
    }).fail(function () {
        box.innerHTML = '<p class="cn-note" style="padding:14px 18px;margin:0">The raw data of this drive is no longer stored.</p>';
    });
}

function canaryNumber(v, dec) {
    var r = Number(Number(v).toFixed(dec));
    if (r === 0) {
        r = 0;
    }
    return r.toLocaleString(undefined, { minimumFractionDigits: dec, maximumFractionDigits: dec });
}

function canaryStopwatch(s) {
    s = Math.max(0, Math.round(s));
    var h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), r = s % 60;
    var two = function (n) { return (n < 10 ? '0' : '') + n; };
    return h ? h + ':' + two(m) + ':' + two(r) : m + ':' + two(r);
}

function canaryNiceStep(span, target) {
    var raw = span / target, mag = Math.pow(10, Math.floor(Math.log10(raw))), f = raw / mag;
    return (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10) * mag;
}

/* Small multiples: one chart per reading on a shared time axis, with a crosshair that reads all of them at once. */
function canaryDraw() {
    var box = document.getElementById('cnCharts');
    if (!box) {
        return;
    }
    var all = canaryState.series;
    if (!all.length) {
        box.innerHTML = '<p class="cn-note" style="padding:14px 18px;margin:0">Nothing was recorded for those readings on this drive.</p>';
        return;
    }
    var W = Math.max(280, box.clientWidth - 36), H = 136, L = 46, top = 14, bot = 6, T = canaryState.duration;
    var x = function (t) { return L + (t / T) * (W - L); };
    var tstep = canaryNiceStep(T / 60, Math.max(3, Math.floor((W - L) / 110))) * 60;
    var unitOf = function (s) { return (!s.unit || s.unit === '%' || s.unit === '°' ? '' : ' ') + (s.unit || ''); };
    box.innerHTML = all.map(function (s, k) {
        var mn = Math.min.apply(null, s.lo), mx = Math.max.apply(null, s.hi);
        var flat = mx - mn < 1e-9;
        var step = flat ? 1 : canaryNiceStep(mx - mn, 3);
        var y0 = flat ? mn - 1 : Math.floor(mn / step) * step, y1 = flat ? mx + 1 : Math.ceil(mx / step) * step;
        var y = function (v) { return top + (1 - (v - y0) / (y1 - y0)) * (H - top - bot); };
        var gap = (T / s.t.length) * 4;
        var line = '', band = '', seg = [];
        var flush = function () {
            if (seg.length > 1) {
                line += seg.map(function (i, j) { return (j ? 'L' : 'M') + x(s.t[i]).toFixed(1) + ' ' + y(s.v[i]).toFixed(1); }).join('');
                band += seg.map(function (i, j) { return (j ? 'L' : 'M') + x(s.t[i]).toFixed(1) + ' ' + y(s.hi[i]).toFixed(1); }).join('')
                    + seg.slice().reverse().map(function (i) { return 'L' + x(s.t[i]).toFixed(1) + ' ' + y(s.lo[i]).toFixed(1); }).join('') + 'Z';
            }
            seg = [];
        };
        s.t.forEach(function (t, i) {
            if (i && t - s.t[i - 1] > gap) {
                flush();
            }
            seg.push(i);
        });
        flush();
        var grid = '';
        var gdec = step >= 1 ? 0 : step >= 0.1 ? 1 : 2;
        for (var g = y0; g <= y1 + step * 1e-6; g += step) {
            grid += '<line class="cn-grid" x1="' + L + '" x2="' + W + '" y1="' + y(g).toFixed(1) + '" y2="' + y(g).toFixed(1) + '"/>'
                + '<text x="' + (L - 8) + '" y="' + (y(g) + 4).toFixed(1) + '" text-anchor="end">' + canaryEscape(canaryNumber(g, gdec)) + '</text>';
        }
        var axis = '';
        if (k === all.length - 1) {
            for (var t = 0; t <= T; t += tstep) {
                axis += '<text x="' + x(t).toFixed(1) + '" y="' + (H + 14) + '" text-anchor="' + (x(t) > W - 24 ? 'end' : 'middle') + '">' + canaryStopwatch(t) + '</text>';
            }
        }
        var summary = flat ? canaryNumber(mn, s.dec) + unitOf(s) + ' throughout' : canaryNumber(mn, s.dec) + ' to ' + canaryNumber(mx, s.dec) + unitOf(s);
        var height = H + (axis ? 18 : 0);
        return '<div class="cn-chart"><div class="cn-chart-head"><span>' + canaryEscape(s.name) + '</span><small data-sum="' + canaryEscape(summary) + '">' + canaryEscape(summary) + '</small></div>'
            + '<svg viewBox="0 0 ' + W + ' ' + height + '" style="height:' + height + 'px" role="img" aria-label="' + canaryEscape(s.name + ' over the drive, ' + summary) + '">'
            + grid + '<path class="cn-band" d="' + band + '"/><path class="cn-line" d="' + line + '"/>'
            + '<line class="cn-cross" y1="' + top + '" y2="' + (H - bot) + '" x1="0" x2="0" visibility="hidden"/>' + axis + '</svg></div>';
    }).join('');
    var charts = Array.prototype.slice.call(box.querySelectorAll('.cn-chart'));
    var move = function (e) {
        var r = charts[0].querySelector('svg').getBoundingClientRect();
        var t = Math.max(0, Math.min(T, ((e.clientX - r.left - L) / (r.width - L)) * T));
        charts.forEach(function (chart, k) {
            var s = all[k], best = 0;
            for (var i = 1; i < s.t.length; i++) {
                if (Math.abs(s.t[i] - t) < Math.abs(s.t[best] - t)) {
                    best = i;
                }
            }
            var cross = chart.querySelector('.cn-cross');
            cross.setAttribute('x1', x(s.t[best]).toFixed(1));
            cross.setAttribute('x2', x(s.t[best]).toFixed(1));
            cross.setAttribute('visibility', 'visible');
            chart.querySelector('small').textContent = canaryNumber(s.v[best], s.dec) + unitOf(s) + ' at ' + canaryStopwatch(s.t[best]);
        });
    };
    var leave = function () {
        charts.forEach(function (chart) {
            chart.querySelector('.cn-cross').setAttribute('visibility', 'hidden');
            var small = chart.querySelector('small');
            small.textContent = small.dataset.sum;
        });
    };
    box.onpointermove = move;
    box.onpointerdown = move;
    box.onpointerleave = leave;
}

$(function () {
    $('#canary-tab').on('show.bs.tab', function () {
        getCanaryReport(canaryVehicle(), '');
    });
    $('#canary-tab').on('hidden.bs.tab', function () {
        $('#canary-tab-pane').html('');
    });
    /* the page may have opened straight onto this section, before this file had a chance to listen */
    if ($('#canary-tab').hasClass('active') && !$('#canary-tab-pane').children().length) {
        getCanaryReport(canaryVehicle(), '');
    }
    canaryBadge();
    var redraw;
    $(window).on('resize', function () {
        clearTimeout(redraw);
        redraw = setTimeout(function () {
            if (document.getElementById('cnCharts') && canaryState.series.length) {
                canaryDraw();
            }
        }, 150);
    });
});

/* A printed report carries the explanations with it: open them for the printer, then put them back. */
window.addEventListener('beforeprint', function () {
    document.querySelectorAll('#canaryRoot details.cn-why:not([open])').forEach(function (d) {
        d.setAttribute('open', '');
        d.setAttribute('data-cn-printing', '');
    });
});
window.addEventListener('afterprint', function () {
    document.querySelectorAll('#canaryRoot details[data-cn-printing]').forEach(function (d) {
        d.removeAttribute('open');
        d.removeAttribute('data-cn-printing');
    });
});
