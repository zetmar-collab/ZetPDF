namespace ZetPDF.Annotations.Forms.Scripting;

/// <summary>
/// Minimalne API Acrobat JavaScript dla skryptów pól, napisane w samym JavaScripcie (bez obiektów CLR): obiekt
/// <c>event</c>, <c>this.getField(n).value</c>, <c>app.alert</c> (tylko zapis komunikatu) i najczęstsze funkcje AForm:
/// AFSimple_Calculate, AFNumber_Format/Keystroke, AFPercent_Format/Keystroke, AFDate_FormatEx/KeystrokeEx,
/// AFDate_Format/Keystroke, AFRange_Validate, AFSpecial_Format/Keystroke. Funkcje hosta: __get, __set, __has, __all, __alert.
/// </summary>
internal static class AcroJsPrelude
{
    public const string Source = """
var event = { value: "", rc: true, willCommit: true, change: "", changeEx: "", name: "", type: "Field", target: null, targetName: "", selStart: 0, selEnd: 0, modifier: false, shift: false };
function __val(s) {
  if (typeof s !== "string") return s;
  if (/^\s*[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?\s*$/.test(s)) return Number(s);
  return s;
}
function Field(n) { this.name = n; }
Object.defineProperty(Field.prototype, "value", {
  get: function () { return __val(__get(this.name)); },
  set: function (v) { __set(this.name, v === null || v === undefined ? "" : String(v)); }
});
Object.defineProperty(Field.prototype, "valueAsString", { get: function () { return __get(this.name); } });
Field.prototype.getArray = function () {
  var all = __all().split("\n"), out = [], p = this.name + ".";
  for (var i = 0; i < all.length; i++) if (all[i].indexOf(p) === 0) out.push(new Field(all[i]));
  return out.length ? out : [this];
};
var __doc = {
  getField: function (n) {
    n = String(n);
    if (__has(n)) return new Field(n);
    var all = __all().split("\n");
    for (var i = 0; i < all.length; i++) if (all[i].indexOf(n + ".") === 0) return new Field(n);
    return null;
  },
  calculateNow: function () { },
  resetForm: function () { }
};
this.getField = __doc.getField;
this.calculateNow = __doc.calculateNow;
var app = { alert: function (m) { __alert(String(m)); return 1; }, beep: function () { }, viewerType: "Reader", platform: "WIN", language: "ENU" };
var console = { println: function (m) { __alert(String(m)); }, show: function () { }, clear: function () { } };

function AFMakeNumber(s) {
  if (typeof s === "number") return s;
  if (s === null || s === undefined) return null;
  s = String(s).replace(/[\s  ]/g, "");
  if (s === "") return null;
  var neg = /^\(.*\)$/.test(s);
  if (neg) s = s.substring(1, s.length - 1);
  s = s.replace(/[^\d.,\-+eE]/g, "");
  if (s.indexOf(",") >= 0 && s.indexOf(".") >= 0) {
    if (s.lastIndexOf(",") > s.lastIndexOf(".")) s = s.replace(/\./g, "").replace(",", ".");
    else s = s.replace(/,/g, "");
  } else if (s.indexOf(",") >= 0) s = s.replace(",", ".");
  if (!/^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$/.test(s)) return null;
  var n = Number(s);
  return isNaN(n) ? null : (neg ? -n : n);
}
function __names(list) {
  if (typeof list === "string") list = list.split(/\s*,\s*/);
  var all = __all().split("\n"), out = [];
  for (var i = 0; i < list.length; i++) {
    var n = String(list[i]);
    if (__has(n)) { out.push(n); continue; }
    for (var j = 0; j < all.length; j++) if (all[j].indexOf(n + ".") === 0) out.push(all[j]);
  }
  return out;
}
function AFSimple_Calculate(op, list) {
  var names = __names(list), res = op === "PRD" ? 1 : 0, count = 0;
  for (var i = 0; i < names.length; i++) {
    var v = AFMakeNumber(__get(names[i]));
    if (v === null) v = 0;
    if (op === "SUM" || op === "AVG") res += v;
    else if (op === "PRD") res *= v;
    else if (op === "MIN") res = count === 0 ? v : Math.min(res, v);
    else if (op === "MAX") res = count === 0 ? v : Math.max(res, v);
    count++;
  }
  if (op === "AVG" && count > 0) res = res / count;
  event.value = res;
}
function __fmtNum(n, dec, sep) {
  dec = Math.max(0, Math.min(10, dec | 0));
  var s = Math.abs(n).toFixed(dec), parts = s.split("."), ip = parts[0], fp = parts.length > 1 ? parts[1] : "";
  var th = [",", "", ".", "", "'"][sep] || "", dp = (sep === 2 || sep === 3) ? "," : ".";
  if (th) ip = ip.replace(/\B(?=(\d{3})+(?!\d))/g, th);
  return ip + (dec > 0 ? dp + fp : "");
}
function AFNumber_Format(nDec, sepStyle, negStyle, currStyle, strCurrency, bCurrencyPrepend) {
  var v = AFMakeNumber(event.value);
  if (v === null) { event.value = ""; return; }
  var s = __fmtNum(v, nDec, sepStyle), cur = strCurrency ? String(strCurrency) : "";
  if (cur) s = bCurrencyPrepend ? cur + s : s + cur;
  if (v < 0 && Number(Math.abs(v).toFixed(Math.max(0, nDec | 0))) !== 0) s = (negStyle === 2 || negStyle === 3) ? "(" + s + ")" : "-" + s;
  event.value = s;
}
function AFNumber_Keystroke(nDec, sepStyle) {
  if (event.willCommit) {
    var s = String(event.value).replace(/^\s+|\s+$/g, "");
    if (s !== "" && AFMakeNumber(s) === null) { event.rc = false; __alert("@Annot.Script.Alert.Number"); }
  } else if (!/^[\d\s.,\-+()]*$/.test(String(event.change))) event.rc = false;
}
function AFPercent_Format(nDec, sepStyle) {
  var v = AFMakeNumber(event.value);
  if (v === null) { event.value = ""; return; }
  event.value = (v < 0 ? "-" : "") + __fmtNum(v * 100, nDec, sepStyle) + "%";
}
function AFPercent_Keystroke(nDec, sepStyle) { AFNumber_Keystroke(nDec, sepStyle); }
function AFRange_Validate(bGreaterThan, nGreaterThan, bLessThan, nLessThan) {
  var v = AFMakeNumber(event.value);
  if (v === null) return;
  if ((bGreaterThan && v < nGreaterThan) || (bLessThan && v > nLessThan)) { event.rc = false; __alert("@Annot.Script.Alert.Range"); }
}
var __months = ["january","february","march","april","may","june","july","august","september","october","november","december"];
function __pad(n, w) { n = String(n); while (n.length < w) n = "0" + n; return n; }
function __fmtDate(d, fmt) {
  return String(fmt).replace(/yyyy|yy|mmmm|mmm|mm|m|dd|d|HH|H|hh|h|MM|M|ss|s|tt/g, function (t) {
    var h12 = d.getHours() % 12 === 0 ? 12 : d.getHours() % 12;
    switch (t) {
      case "yyyy": return __pad(d.getFullYear(), 4);
      case "yy": return __pad(d.getFullYear() % 100, 2);
      case "mmmm": var mn = __months[d.getMonth()]; return mn.charAt(0).toUpperCase() + mn.substring(1);
      case "mmm": var ms = __months[d.getMonth()].substring(0, 3); return ms.charAt(0).toUpperCase() + ms.substring(1);
      case "mm": return __pad(d.getMonth() + 1, 2);
      case "m": return String(d.getMonth() + 1);
      case "dd": return __pad(d.getDate(), 2);
      case "d": return String(d.getDate());
      case "HH": return __pad(d.getHours(), 2);
      case "H": return String(d.getHours());
      case "hh": return __pad(h12, 2);
      case "h": return String(h12);
      case "MM": return __pad(d.getMinutes(), 2);
      case "M": return String(d.getMinutes());
      case "ss": return __pad(d.getSeconds(), 2);
      case "s": return String(d.getSeconds());
      case "tt": return d.getHours() < 12 ? "am" : "pm";
    }
    return t;
  });
}
function __mkDate(y, mo, d, h, mi, s) {
  if (y < 100) y += y < 50 ? 2000 : 1900;
  if (mo < 0 || mo > 11 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 59) return null;
  var r = new Date(y, mo, d, h, mi, s);
  return r.getMonth() === mo && r.getDate() === d ? r : null;
}
function __parseDate(str, fmt) {
  str = String(str).replace(/^\s+|\s+$/g, "");
  if (str === "") return null;
  var iso = /^(\d{4})-(\d{1,2})-(\d{1,2})(?:[T ](\d{1,2}):(\d{2})(?::(\d{2}))?)?$/.exec(str);
  if (iso) return __mkDate(+iso[1], +iso[2] - 1, +iso[3], +(iso[4] || 0), +(iso[5] || 0), +(iso[6] || 0));
  var tokens = String(fmt).match(/yyyy|yy|mmmm|mmm|mm|m|dd|d|HH|H|hh|h|MM|M|ss|s|tt/g) || [];
  var parts = str.match(/\d+|[A-Za-z]+/g) || [];
  var y = new Date().getFullYear(), mo = 0, d = 1, h = 0, mi = 0, s = 0, pm = null, k = 0;
  for (var i = 0; i < tokens.length && k < parts.length; i++) {
    var t = tokens[i], p = parts[k];
    if (t === "tt") { if (/^[ap]/i.test(p)) { pm = /^p/i.test(p); k++; } continue; }
    if (t === "mmm" || t === "mmmm") {
      var idx = -1;
      for (var j = 0; j < 12; j++) if (__months[j].indexOf(p.toLowerCase().substring(0, 3)) === 0) idx = j;
      if (idx < 0) return null;
      mo = idx; k++; continue;
    }
    if (!/^\d+$/.test(p)) return null;
    var n = +p; k++;
    if (t === "yyyy" || t === "yy") y = n;
    else if (t === "mm" || t === "m") mo = n - 1;
    else if (t === "dd" || t === "d") d = n;
    else if (t === "HH" || t === "H" || t === "hh" || t === "h") h = n;
    else if (t === "MM" || t === "M") mi = n;
    else if (t === "ss" || t === "s") s = n;
  }
  if (k < parts.length && tokens.length > 0) return null;
  if (pm === true && h < 12) h += 12;
  if (pm === false && h === 12) h = 0;
  return __mkDate(y, mo, d, h, mi, s);
}
function AFDate_FormatEx(fmt) {
  var s = String(event.value);
  if (s === "") return;
  var d = __parseDate(s, fmt);
  if (d) event.value = __fmtDate(d, fmt);
}
function AFDate_KeystrokeEx(fmt) {
  if (!event.willCommit) return;
  var s = String(event.value);
  if (s !== "" && !__parseDate(s, fmt)) { event.rc = false; __alert("@Annot.Script.Alert.Date"); }
}
var __dateFormats = ["m/d", "m/d/yy", "mm/dd/yy", "mm/yy", "d-mmm", "d-mmm-yy", "dd-mmm-yy", "yy-mm-dd", "mmm-yy", "mmmm-yy", "mmm d, yyyy", "mmmm d, yyyy", "m/d/yy h:MM tt", "m/d/yy HH:MM"];
function AFDate_Format(n) { AFDate_FormatEx(__dateFormats[n] || "m/d/yy"); }
function AFDate_Keystroke(n) { AFDate_KeystrokeEx(__dateFormats[n] || "m/d/yy"); }
function AFSpecial_Format(psf) {
  var s = String(event.value).replace(/\D/g, "");
  if (s === "") return;
  if (psf === 0) event.value = s.substring(0, 5);
  else if (psf === 1) event.value = s.substring(0, 5) + (s.length > 5 ? "-" + s.substring(5, 9) : "");
  else if (psf === 2) event.value = s.length > 7 ? "(" + s.substring(0, 3) + ") " + s.substring(3, 6) + "-" + s.substring(6, 10) : s.substring(0, 3) + "-" + s.substring(3, 7);
  else if (psf === 3) event.value = s.substring(0, 3) + "-" + s.substring(3, 5) + "-" + s.substring(5, 9);
}
function AFSpecial_Keystroke(psf) {
  if (!event.willCommit) { if (!/^[\d\s()\-.]*$/.test(String(event.change))) event.rc = false; return; }
  var n = String(event.value).replace(/\D/g, "").length;
  var ok = psf === 0 ? n === 5 : psf === 1 ? n === 9 : psf === 2 ? (n === 7 || n === 10) : n === 9;
  if (String(event.value) !== "" && !ok) { event.rc = false; __alert("@Annot.Script.Alert.Format"); }
}
""";
}
