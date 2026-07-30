const state = {
  dataPage: 1,
  dataPageSize: 50,
  dataTotalPages: 0,
  importPage: 1,
  importTotalPages: 0,
  activeImportLogId: null,
  importChangePage: 1,
  importChangeType: "",
  activeView: "data",
  manualImportRunning: false,
  aiEnabled: false,
  aiMaxRecords: 0,
  pendingAiQuery: null,
  adminStatus: null,
  adminSetupPrompted: false,
};

let manualImportTimer;

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

document.addEventListener("DOMContentLoaded", () => {
  bindEvents();
  checkHealth();
  loadAiStatus();
  loadAdminStatus(true);
  if (["#imports", "#settings"].includes(location.hash)) switchView(location.hash.slice(1));
  loadFilterOptions().finally(loadSapData);
});

function bindEvents() {
  $("#filterForm").addEventListener("submit", event => {
    event.preventDefault();
    state.dataPage = 1;
    loadSapData();
  });
  $("#resetFilters").addEventListener("click", resetFilters);
  $("#pageSize").addEventListener("change", event => {
    state.dataPageSize = Number(event.target.value);
    state.dataPage = 1;
    loadSapData();
  });
  $("#previousPage").addEventListener("click", () => changeDataPage(-1));
  $("#nextPage").addEventListener("click", () => changeDataPage(1));
  $("#sortBy").addEventListener("change", () => { state.dataPage = 1; loadSapData(); });
  $("#sortDirection").addEventListener("click", event => {
    const button = event.currentTarget;
    const next = button.dataset.direction === "desc" ? "asc" : "desc";
    button.dataset.direction = next;
    button.textContent = next === "desc" ? "↓" : "↑";
    loadSapData();
  });
  $("#dataRows").addEventListener("click", event => {
    const button = event.target.closest("[data-record-id]");
    if (button) openSapDetail(button.dataset.recordId);
  });
  $$(".nav-item").forEach(button => button.addEventListener("click", () => switchView(button.dataset.view)));
  $("#searchImports").addEventListener("click", () => { state.importPage = 1; loadImportLogs(); });
  $("#runManualImport").addEventListener("click", startManualImport);
  $("#uploadForm").addEventListener("submit", uploadAndImport);
  $("#generateAiPlan").addEventListener("click", generateAiPlan);
  $("#interpretAiFilter").addEventListener("click", interpretAiFilter);
  $("#aiFilterQuestion").addEventListener("keydown", event => {
    if (event.key === "Enter") { event.preventDefault(); interpretAiFilter(); }
  });
  $("#aiFilterPreview").addEventListener("click", event => {
    if (event.target.closest("[data-apply-ai-filter]")) applyAiFilter();
    if (event.target.closest("[data-cancel-ai-filter]")) clearAiFilterPreview();
  });
  $("#importSearch").addEventListener("keydown", event => {
    if (event.key === "Enter") { state.importPage = 1; loadImportLogs(); }
  });
  $("#previousImportPage").addEventListener("click", () => changeImportPage(-1));
  $("#nextImportPage").addEventListener("click", () => changeImportPage(1));
  $("#importRows").addEventListener("click", event => {
    const button = event.target.closest("[data-import-id]");
    if (button) openImportDetail(button.dataset.importId);
  });
  $("#adminSetupForm").addEventListener("submit", setupAdmin);
  $("#adminLoginForm").addEventListener("submit", loginAdmin);
  $("#adminLogout").addEventListener("click", logoutAdmin);
  $("#aiSettingsForm").addEventListener("submit", saveAiApiKey);
  $("#testApiKey").addEventListener("click", testAiApiKey);
  $("#removeApiKey").addEventListener("click", removeAiApiKey);
  $("#toggleApiKey").addEventListener("click", toggleApiKeyVisibility);
  $$('[data-close-dialog]').forEach(button => button.addEventListener("click", () => $("#detailDialog").close()));
  $("#detailDialog").addEventListener("click", event => {
    if (event.target === event.currentTarget) event.currentTarget.close();
  });
  document.addEventListener("keydown", event => {
    if (event.key === "/" && !/input|select|textarea/i.test(document.activeElement.tagName)) {
      event.preventDefault();
      $("#search").focus();
    }
  });
  ["salesOffice", "plantCode", "siId", "customer", "oilSc", "oilSo", "oilPo"]
    .forEach(id => $("#" + id).addEventListener("input", updateAdvancedCount));
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: { Accept: "application/json", ...(options.headers || {}) },
  });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new Error(body.detail || body.title || `Yêu cầu thất bại (${response.status})`);
  }
  return response.json();
}

async function adminApi(path, options = {}) {
  return api(path, {
    ...options,
    headers: {
      "X-SapDataSync-Admin": "1",
      ...(options.body && !(options.body instanceof FormData) ? { "Content-Type": "application/json" } : {}),
      ...(options.headers || {}),
    },
  });
}

async function checkHealth() {
  const health = $("#systemHealth");
  try {
    const result = await api("/api/health");
    health.className = "system-health healthy";
    health.querySelector("span:last-child").textContent = result.status === "Healthy" ? "Dữ liệu sẵn sàng" : result.status;
  } catch {
    health.className = "system-health unhealthy";
    health.querySelector("span:last-child").textContent = "Mất kết nối";
  }
}

async function loadAiStatus() {
  try {
    const status = await api("/api/ai/status");
    state.aiEnabled = Boolean(status.enabled);
    state.aiMaxRecords = Number(status.maxRecords || 0);
    $("#aiPlanner").hidden = !state.aiEnabled;
    $("#aiModelBadge").textContent = `${status.provider} · ${status.model}`;
    $("#aiScopeNote").textContent = `AI phân tích tối đa ${formatNumber(status.maxRecords)} bản ghi đầu tiên khớp bộ lọc hiện tại.`;
  } catch {
    state.aiEnabled = false;
    $("#aiPlanner").hidden = true;
  }
}

async function loadAdminStatus(autoOpenSetup = false) {
  try {
    const status = await api("/api/admin/status");
    state.adminStatus = status;
    renderAdminStatus(status);
    if (autoOpenSetup && status.setupRequired && !state.adminSetupPrompted && !location.hash) {
      state.adminSetupPrompted = true;
      switchView("settings");
      showToast("Hãy hoàn tất thiết lập quản trị lần đầu.");
    }
  } catch (error) {
    setAdminFeedback(error.message, "error");
  }
}

function renderAdminStatus(status) {
  const setupRequired = Boolean(status.setupRequired);
  const authenticated = Boolean(status.authenticated);
  $("#firstRunGuide").hidden = !setupRequired;
  $("#adminAuthPanel").hidden = authenticated;
  $("#adminSetupForm").hidden = !setupRequired || authenticated;
  $("#adminLoginForm").hidden = setupRequired || authenticated;
  $("#adminSettingsPanel").hidden = !authenticated;
  $("#uploadCard").hidden = !authenticated;
  $("#manualImportCard").hidden = !authenticated;
  $("#settingsNav").classList.toggle("attention", setupRequired);

  if (!authenticated) return;
  const configured = Boolean(status.aiConfigured);
  $("#adminAiStatusDot").classList.toggle("configured", configured);
  $("#adminAiStatusText").textContent = configured
    ? `AI đã được cấu hình (${status.apiKeyMasked || "đã ẩn"})`
    : "AI đang tắt — chưa có API key";
  $("#adminAiModelText").textContent = `${status.provider} · ${status.model}`;
  $("#removeApiKey").disabled = !configured;
  $("#adminApiKey").placeholder = configured
    ? "Nhập key mới để thay thế key đang lưu"
    : "Dán API key tại đây";
}

async function setupAdmin(event) {
  event.preventDefault();
  const password = $("#setupPassword").value;
  if (password !== $("#setupPasswordConfirm").value) {
    showToast("Hai lần nhập mật khẩu chưa giống nhau.");
    return;
  }

  await runAdminAction(event.submitter, "Đang tạo…", async () => {
    const status = await adminApi("/api/admin/setup", {
      method: "POST",
      body: JSON.stringify({ password }),
    });
    $("#adminSetupForm").reset();
    state.adminStatus = status;
    renderAdminStatus(status);
    showToast("Đã tạo tài khoản quản trị. Hãy cấu hình API key nếu cần dùng AI.");
  });
}

async function loginAdmin(event) {
  event.preventDefault();
  const password = $("#loginPassword").value;
  await runAdminAction(event.submitter, "Đang đăng nhập…", async () => {
    const status = await adminApi("/api/admin/login", {
      method: "POST",
      body: JSON.stringify({ password }),
    });
    $("#adminLoginForm").reset();
    state.adminStatus = status;
    renderAdminStatus(status);
    showToast("Đăng nhập quản trị thành công.");
  });
}

async function logoutAdmin() {
  try {
    await adminApi("/api/admin/logout", { method: "POST" });
    await loadAdminStatus();
    $("#adminApiKey").value = "";
    setAdminFeedback("Đã đăng xuất.", "success");
  } catch (error) {
    setAdminFeedback(error.message, "error");
  }
}

async function saveAiApiKey(event) {
  event.preventDefault();
  const apiKey = $("#adminApiKey").value.trim();
  if (!apiKey) {
    setAdminFeedback("Hãy nhập API key mới trước khi lưu.", "error");
    return;
  }

  await runAdminAction(event.submitter, "Đang lưu…", async () => {
    const status = await adminApi("/api/admin/settings/ai", {
      method: "PUT",
      body: JSON.stringify({ apiKey }),
    });
    $("#adminApiKey").value = "";
    state.adminStatus = status;
    renderAdminStatus(status);
    setAdminFeedback("Đã mã hóa và lưu API key phía server.", "success");
    await loadAiStatus();
  });
}

async function testAiApiKey() {
  const button = $("#testApiKey");
  await runAdminAction(button, "Đang kiểm tra…", async () => {
    const result = await adminApi("/api/admin/settings/ai/test", {
      method: "POST",
      body: JSON.stringify({ apiKey: $("#adminApiKey").value.trim() || null }),
    });
    setAdminFeedback(result.message, "success");
  });
}

async function removeAiApiKey() {
  if (!window.confirm("Xóa API key đã lưu và tắt toàn bộ chức năng AI?")) return;
  const button = $("#removeApiKey");
  await runAdminAction(button, "Đang xóa…", async () => {
    const status = await adminApi("/api/admin/settings/ai", { method: "DELETE" });
    state.adminStatus = status;
    renderAdminStatus(status);
    setAdminFeedback("Đã xóa API key. Các chức năng dữ liệu thông thường vẫn hoạt động.", "success");
    await loadAiStatus();
  });
}

function toggleApiKeyVisibility() {
  const input = $("#adminApiKey");
  const show = input.type === "password";
  input.type = show ? "text" : "password";
  $("#toggleApiKey").textContent = show ? "Ẩn" : "Hiện";
}

async function runAdminAction(button, busyText, action) {
  const originalText = button?.textContent;
  if (button) {
    button.disabled = true;
    button.textContent = busyText;
  }
  setAdminFeedback("", "");
  try {
    await action();
  } catch (error) {
    setAdminFeedback(error.message, "error");
    showToast(error.message);
  } finally {
    if (button) {
      button.disabled = button.id === "removeApiKey" && !state.adminStatus?.aiConfigured;
      button.textContent = originalText;
    }
  }
}

function setAdminFeedback(message, stateName) {
  const feedback = $("#apiKeyFeedback");
  if (!feedback) return;
  feedback.textContent = message || "";
  feedback.dataset.state = stateName || "";
}

async function loadFilterOptions() {
  try {
    const options = await api("/api/sap-data/filter-options");
    fillDatalist("#productOptions", options.products);
    fillDatalist("#salesOrganizationOptions", options.salesOrganizations);
    fillDatalist("#salesOfficeOptions", options.salesOffices);
    fillDatalist("#plantCodeOptions", options.plantCodes);
    fillSelect("#siStatus", options.siStatuses, "Tất cả trạng thái");
    renderScenarioOptions(options.businessScenarios);
  } catch (error) {
    showToast(error.message);
  }
}

function fillDatalist(selector, values = []) {
  $(selector).innerHTML = values.map(value => `<option value="${escapeHtml(value)}"></option>`).join("");
}

function fillSelect(selector, values = [], emptyLabel) {
  const select = $(selector);
  select.innerHTML = `<option value="">${escapeHtml(emptyLabel)}</option>` +
    values.map(value => `<option value="${escapeHtml(value)}">${escapeHtml(value)}</option>`).join("");
}

function renderScenarioOptions(values = []) {
  const scenarioCodes = {
    "DEST-OIL Purchase": "PDO",
    "Purchase Safety Stock": "PWS",
    "Sales Direct Shipment": "SDS",
    "Sales Warehouse Shipment": "SWS",
  };
  const all = values.length ? values : Object.keys(scenarioCodes);
  $("#scenarioOptions").innerHTML = all.map(value => {
    const code = scenarioCodes[value] || value;
    const label = scenarioCodes[value] ? `${code} · ${value}` : value;
    const checked = Object.values(scenarioCodes).includes(code);
    return `<label class="check-chip"><input type="checkbox" value="${escapeHtml(value)}" data-code="${escapeHtml(code)}" ${checked ? "checked" : ""}><span>${escapeHtml(label)}</span></label>`;
  }).join("");
}

function buildSapQuery() {
  const params = new URLSearchParams({
    page: String(state.dataPage),
    pageSize: String(state.dataPageSize),
    sortBy: $("#sortBy").value,
    sortDirection: $("#sortDirection").dataset.direction,
  });
  const mappings = {
    product: "product", salesOrganization: "salesOrganization", search: "search",
    siStatus: "siStatus", createdFrom: "createdFrom", createdTo: "createdTo",
    salesOffice: "salesOffice", plantCode: "plantCode", siId: "siId", customer: "customer",
    oilSc: "oilSc", oilSo: "oilSo", oilPo: "oilPo",
  };
  Object.entries(mappings).forEach(([id, key]) => {
    const value = $("#" + id).value.trim();
    if (value) params.set(key, value);
  });
  const scenarios = $$('#scenarioOptions input:checked').map(input => input.value);
  if (scenarios.length) params.set("businessScenario", scenarios.join(","));
  return params;
}

function buildAiQuery() {
  const query = Object.fromEntries(buildSapQuery().entries());
  query.page = 1;
  query.pageSize = Math.max(10, state.aiMaxRecords || 50);
  return query;
}

async function interpretAiFilter() {
  if (!state.aiEnabled) return;
  const question = $("#aiFilterQuestion").value.trim();
  if (!question) {
    showToast("Hãy nhập câu hỏi cần chuyển thành bộ lọc.");
    return;
  }

  const button = $("#interpretAiFilter");
  const preview = $("#aiFilterPreview");
  button.disabled = true;
  button.textContent = "AI đang đọc câu hỏi…";
  preview.hidden = false;
  preview.innerHTML = `<div class="ai-plan-loading"><span class="spinner"></span><span>Đang tạo bộ lọc nháp…</span></div>`;

  try {
    const response = await api("/api/ai/filters", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ question }),
    });
    state.pendingAiQuery = response.query || null;
    renderAiFilterPreview(response);
  } catch (error) {
    state.pendingAiQuery = null;
    preview.innerHTML = `<div class="error-box">${escapeHtml(error.message)}</div>`;
    showToast(error.message);
  } finally {
    button.disabled = false;
    button.textContent = "Tạo lại bộ lọc nháp";
  }
}

function renderAiFilterPreview(response) {
  const query = response.query || {};
  const labels = {
    product: "Product", salesOrganization: "Sales Organization", businessScenario: "Business Scenario",
    siStatus: "SI Status", salesOffice: "Sales Office", plantCode: "PlantCode", siId: "SI ID",
    customer: "Customer", oilSc: "OIL SC", oilSo: "OIL SO", oilPo: "OIL PO", search: "Tìm nhanh",
    createdFrom: "Từ ngày", createdTo: "Đến ngày", sortBy: "Sắp xếp", sortDirection: "Chiều",
  };
  const ignored = new Set(["page", "pageSize"]);
  const chips = Object.entries(query)
    .filter(([key, value]) => !ignored.has(key) && value !== null && value !== undefined && value !== "")
    .map(([key, value]) => `<span><b>${escapeHtml(labels[key] || key)}:</b> ${escapeHtml(value)}</span>`)
    .join("");
  const assumptions = Array.isArray(response.assumptions) ? response.assumptions : [];

  $("#aiFilterPreview").innerHTML = `
    <div class="ai-filter-preview-header">
      <div><span class="section-kicker">Bản nháp · chưa áp dụng</span><strong>${escapeHtml(response.summary || "Bộ lọc do AI đề xuất")}</strong></div>
      <span class="ai-confirmation-badge">Cần xác nhận</span>
    </div>
    <div class="ai-filter-chips">${chips || "<span>Không có điều kiện cụ thể</span>"}</div>
    ${renderAiNotes("Điểm cần kiểm tra", assumptions)}
    <div class="ai-filter-actions">
      <button class="secondary-button" type="button" data-cancel-ai-filter>Hủy</button>
      <button class="primary-button" type="button" data-apply-ai-filter>Áp dụng bộ lọc</button>
    </div>`;
}

function applyAiFilter() {
  const query = state.pendingAiQuery;
  if (!query) return;

  const fieldIds = ["product", "salesOrganization", "search", "siStatus", "createdFrom", "createdTo",
    "salesOffice", "plantCode", "siId", "customer", "oilSc", "oilSo", "oilPo"];
  fieldIds.forEach(id => { $("#" + id).value = query[id] || ""; });

  const requestedScenarios = String(query.businessScenario || "")
    .split(",").map(value => value.trim().toLowerCase()).filter(Boolean);
  $$('#scenarioOptions input').forEach(input => {
    input.checked = requestedScenarios.some(value =>
      value === String(input.value).toLowerCase() || value === String(input.dataset.code || "").toLowerCase());
  });

  if (query.sortBy && [...$("#sortBy").options].some(option => option.value === query.sortBy)) {
    $("#sortBy").value = query.sortBy;
  }
  const direction = query.sortDirection === "asc" ? "asc" : "desc";
  $("#sortDirection").dataset.direction = direction;
  $("#sortDirection").textContent = direction === "desc" ? "↓" : "↑";
  updateAdvancedCount();
  state.dataPage = 1;
  clearAiFilterPreview();
  loadSapData();
  $("#resultsHeading").scrollIntoView({ behavior: "smooth", block: "start" });
}

function clearAiFilterPreview() {
  state.pendingAiQuery = null;
  $("#aiFilterPreview").hidden = true;
  $("#aiFilterPreview").innerHTML = "";
}

async function generateAiPlan() {
  if (!state.aiEnabled) return;
  const button = $("#generateAiPlan");
  const result = $("#aiPlanResult");
  button.disabled = true;
  button.textContent = "AI đang lập kế hoạch…";
  result.hidden = false;
  result.innerHTML = `<div class="ai-plan-loading"><span class="spinner"></span><span>Đang phân tích tập dữ liệu đã lọc…</span></div>`;

  try {
    const response = await api("/api/ai/plans", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ goal: $("#aiGoal").value.trim(), query: buildAiQuery() }),
    });
    renderAiPlan(response);
  } catch (error) {
    result.innerHTML = `<div class="error-box">${escapeHtml(error.message)}</div>`;
    showToast(error.message);
  } finally {
    button.disabled = false;
    button.textContent = "Tạo lại kế hoạch AI";
  }
}

function renderAiPlan(response) {
  const plan = response.plan || {};
  const actions = Array.isArray(plan.actions) ? plan.actions : [];
  const risks = Array.isArray(plan.risks) ? plan.risks : [];
  const assumptions = Array.isArray(plan.assumptions) ? plan.assumptions : [];
  $("#aiPlanResult").innerHTML = `
    <header class="ai-plan-title">
      <div><span class="section-kicker">Kế hoạch đề xuất</span><h3>${escapeHtml(plan.title || "Kế hoạch AI")}</h3></div>
      <span>${formatNumber(response.analyzedRecords)} / ${formatNumber(response.totalMatchingRecords)} bản ghi</span>
    </header>
    <p class="ai-plan-summary">${escapeHtml(plan.executiveSummary || "")}</p>
    <ol class="ai-action-list">
      ${actions.map(action => {
        const ids = Array.isArray(action.relatedShippingInstructionIds) ? action.relatedShippingInstructionIds : [];
        return `<li>
          <span class="ai-priority">P${escapeHtml(action.priority)}</span>
          <div><strong>${escapeHtml(action.action || "")}</strong><p>${escapeHtml(action.reason || "")}</p>
          ${ids.length ? `<small>SI liên quan: ${ids.map(escapeHtml).join(", ")}</small>` : ""}</div>
        </li>`;
      }).join("")}
    </ol>
    ${renderAiNotes("Rủi ro cần kiểm tra", risks)}
    ${renderAiNotes("Giả định của AI", assumptions)}
    <footer class="ai-disclaimer">${escapeHtml(response.disclaimer || "")}</footer>`;
}

function renderAiNotes(title, items) {
  if (!items.length) return "";
  return `<section class="ai-notes"><h4>${escapeHtml(title)}</h4><ul>${items.map(item => `<li>${escapeHtml(item)}</li>`).join("")}</ul></section>`;
}

async function loadSapData() {
  setLoading("data", true);
  try {
    const result = await api(`/api/sap-data?${buildSapQuery()}`);
    state.dataTotalPages = result.totalPages;
    renderSapRows(result.items);
    $("#totalRecords").textContent = formatNumber(result.totalItems);
    $("#pageInfo").textContent = result.totalPages ? `Trang ${result.page} / ${result.totalPages}` : "Không có dữ liệu";
    $("#previousPage").disabled = result.page <= 1;
    $("#nextPage").disabled = result.page >= result.totalPages;
    $("#dataEmpty").hidden = result.items.length > 0;
  } catch (error) {
    renderSapRows([]);
    $("#dataEmpty").hidden = false;
    showToast(error.message);
  } finally {
    setLoading("data", false);
  }
}

function renderSapRows(items) {
  $("#dataRows").innerHTML = items.map(item => `
    <tr>
      <td class="id-cell">${display(item.shippingInstructionsId)}<span class="secondary-id">${display(item.uniqueNumber)}</span></td>
      <td><span class="status-pill ${statusClass(item.siStatus)}">${display(item.siStatus)}</span></td>
      <td class="customer-cell" title="${escapeHtml(item.customerName || "")}">${display(item.customerName)}</td>
      <td>${display(item.businessScenario)}</td>
      <td>${display(item.plantCode || item.sellingPlant)}</td>
      <td>${displayQuantity(item.quantity, item.uom)}</td>
      <td>${display(item.oilSc)}</td>
      <td>${formatSapDate(item.siCreatedOn)}</td>
      <td>${formatSapDate(item.estimatedDeparture)}</td>
      <td><button class="row-open" type="button" data-record-id="${item.id}" aria-label="Xem chi tiết ${escapeHtml(item.shippingInstructionsId || item.id)}">→</button></td>
    </tr>`).join("");
}

async function openSapDetail(id) {
  openDialogLoading("Chi tiết Shipping Instruction");
  try {
    const item = await api(`/api/sap-data/${encodeURIComponent(id)}`);
    const siId = item.fields["Shipping Instructions ID"] || `Bản ghi #${item.id}`;
    $("#detailEyebrow").textContent = "149 trường dữ liệu · Chỉ đọc";
    $("#detailTitle").textContent = siId;
    $("#detailMeta").innerHTML = [
      ["Product", item.product], ["Sales Organization", item.salesOrganization],
      ["Dòng Excel", item.sourceRowNumber], ["Cập nhật", formatDateTime(item.updatedAt)]
    ].map(([label, value]) => `<div class="meta-item"><span>${label}</span><strong>${display(value)}</strong></div>`).join("");

    const groups = groupFields(item.fields);
    $("#detailContent").innerHTML = groups.map((group, index) => `
      <details class="detail-section" ${index < 2 ? "open" : ""}>
        <summary>${escapeHtml(group.label)} · ${group.fields.length} trường</summary>
        <dl class="detail-grid">${group.fields.map(([name, value]) => `
          <div class="detail-field"><dt>${escapeHtml(name)}</dt><dd class="${value ? "" : "empty"}">${display(value)}</dd></div>`).join("")}
        </dl>
      </details>`).join("");
  } catch (error) {
    $("#detailContent").innerHTML = `<div class="error-box">${escapeHtml(error.message)}</div>`;
  }
}

function groupFields(fields) {
  const groups = [
    { key: "overview", label: "Đơn hàng & khách hàng", fields: [] },
    { key: "product", label: "Sản phẩm & số lượng", fields: [] },
    { key: "schedule", label: "Kế hoạch & ngày giao hàng", fields: [] },
    { key: "quality", label: "Mẫu & chất lượng", fields: [] },
    { key: "logistics", label: "Booking & vận chuyển", fields: [] },
    { key: "finance", label: "Chứng từ & thanh toán", fields: [] },
    { key: "audit", label: "Trạng thái & lịch sử cập nhật", fields: [] },
    { key: "other", label: "Thông tin khác", fields: [] },
  ];
  Object.entries(fields).forEach(entry => groups.find(group => group.key === classifyField(entry[0])).fields.push(entry));
  return groups.filter(group => group.fields.length);
}

function classifyField(name) {
  const text = name.toLowerCase();
  if (/sample|lab|quality|rfa|pss|tested|courier/.test(text)) return "quality";
  if (/invoice|payment|freight|gr\/ir|accounting|awb/.test(text)) return "finance";
  if (/booking|vessel|container|seal|stuffing|packing|weight|ship line|pol$|pod$|country/.test(text)) return "logistics";
  if (/date|month|year|arrival|departure/.test(text)) return "schedule";
  if (/revise|revised|status|comments|created by|remarks updated|process/.test(text)) return "audit";
  if (/grade|item|quantity|uom|packaging|batch|material|product|pallet/.test(text)) return "product";
  if (/shipping instructions|business scenario|office|customer|plant|origin|inco|trader|supplier|oil sales|oil so|oil purchase|unique/.test(text)) return "overview";
  return "other";
}

function switchView(view) {
  state.activeView = view;
  $$(".nav-item").forEach(button => button.classList.toggle("active", button.dataset.view === view));
  $$(".view").forEach(section => {
    const active = section.id === `${view}View`;
    section.classList.toggle("active", active);
    section.hidden = !active;
  });
  if (view === "imports") {
    loadImportLogs();
    loadManualImportStatus();
  }
  if (view === "settings") loadAdminStatus();
  history.replaceState(null, "", `#${view}`);
}

async function loadImportLogs() {
  $("#importsLoading").hidden = false;
  $("#importsEmpty").hidden = true;
  const params = new URLSearchParams({ page: state.importPage, pageSize: 25 });
  if ($("#importSearch").value.trim()) params.set("search", $("#importSearch").value.trim());
  if ($("#importStatus").value) params.set("status", $("#importStatus").value);
  try {
    const result = await api(`/api/import-logs?${params}`);
    state.importTotalPages = result.totalPages;
    $("#importTotal").textContent = `${formatNumber(result.totalItems)} lần import`;
    $("#importPageInfo").textContent = result.totalPages ? `Trang ${result.page} / ${result.totalPages}` : "Không có dữ liệu";
    $("#previousImportPage").disabled = result.page <= 1;
    $("#nextImportPage").disabled = result.page >= result.totalPages;
    $("#importsEmpty").hidden = result.items.length > 0;
    $("#importRows").innerHTML = result.items.map(item => `
      <tr><td class="id-cell">${display(item.fileName)}<span class="secondary-id">${display(item.product)} · ${display(item.salesOrganization)}</span></td>
      <td><span class="status-pill ${statusClass(item.status)}">${display(item.status)}</span></td>
      <td>${formatDateTime(item.startedAt)}</td><td>${formatNumber(item.totalRows)}</td>
      <td>${formatNumber(item.insertedRows)}</td><td>${formatNumber(item.updatedRows)}</td>
      <td>${formatNumber(item.deletedRows)}</td><td>${formatNumber(item.unchangedRows)}</td><td>${formatNumber(item.errorRows)}</td>
      <td><button class="row-open" type="button" data-import-id="${item.id}" aria-label="Xem chi tiết import">→</button></td></tr>`).join("");
  } catch (error) {
    showToast(error.message);
  } finally {
    $("#importsLoading").hidden = true;
  }
}

async function startManualImport() {
  const confirmed = window.confirm(
    "Chạy ETL ngay với file hiện có trong data/source? File đã import thành công sẽ được tự động bỏ qua."
  );
  if (!confirmed) return;

  const button = $("#runManualImport");
  button.disabled = true;
  button.textContent = "Đang gửi yêu cầu…";
  try {
    const status = await adminApi("/api/imports/run", { method: "POST" });
    renderManualImportStatus(status);
    scheduleManualImportPoll();
  } catch (error) {
    renderManualImportError(error.message);
    showToast(error.message);
  }
}

async function uploadAndImport(event) {
  event.preventDefault();
  const input = $("#uploadFile");
  const file = input.files?.[0];
  if (!file) {
    showToast("Hãy chọn một file Excel .xlsx.");
    return;
  }

  const confirmed = window.confirm(
    `Upload và đồng bộ file ${file.name}?\n\nNếu Soft Delete đang bật, file phải là snapshot đầy đủ của Product/Sales Organization hiện tại.`
  );
  if (!confirmed) return;

  const button = $("#uploadAndImport");
  button.disabled = true;
  button.textContent = "Đang upload…";
  $("#uploadStatus").dataset.state = "running";
  $("#uploadStatus").textContent = `Đang tải ${file.name} lên máy chủ…`;

  try {
    const form = new FormData();
    form.append("file", file);
    const uploaded = await adminApi("/api/uploads", { method: "POST", body: form });
    $("#uploadStatus").textContent = uploaded.alreadyExisted
      ? `File đã có trên máy chủ (${uploaded.storedFileName}). Đang yêu cầu ETL kiểm tra…`
      : `Đã lưu ${uploaded.storedFileName}. Đang yêu cầu ETL import…`;
    const status = await adminApi("/api/imports/run", { method: "POST" });
    renderManualImportStatus(status);
    scheduleManualImportPoll();
    input.value = "";
    $("#uploadStatus").dataset.state = "success";
    $("#uploadStatus").textContent = "Upload hoàn tất. Theo dõi kết quả trong Lịch sử import bên dưới.";
  } catch (error) {
    $("#uploadStatus").dataset.state = "error";
    $("#uploadStatus").textContent = error.message;
    showToast(error.message);
  } finally {
    button.disabled = false;
    button.textContent = "Upload & Import";
  }
}

async function loadManualImportStatus(silent = false) {
  try {
    const status = await api("/api/imports/status");
    const wasRunning = state.manualImportRunning;
    renderManualImportStatus(status);
    if (status.running) {
      scheduleManualImportPoll();
    } else if (wasRunning) {
      loadImportLogs();
      loadSapData();
      loadFilterOptions();
    }
  } catch (error) {
    renderManualImportError("ETL Worker chưa sẵn sàng.");
    if (!silent) showToast(error.message);
  }
}

function renderManualImportStatus(status) {
  state.manualImportRunning = Boolean(status.running);
  const statusElement = $("#manualImportStatus");
  const successful = !status.running && status.exitCode === 0;
  const failed = !status.running && status.exitCode !== null && status.exitCode !== 0;
  statusElement.dataset.state = status.running ? "running" : successful ? "success" : failed ? "error" : "idle";
  $("#manualImportMessage").textContent = status.message || "ETL Worker đã sẵn sàng.";
  const timestamp = status.running ? status.startedAt : status.completedAt;
  $("#manualImportTime").textContent = timestamp ? `· ${formatDateTime(timestamp)}` : "";
  const button = $("#runManualImport");
  button.disabled = status.running;
  button.textContent = status.running ? "Đang import…" : "Chạy import ngay";
}

function renderManualImportError(message) {
  state.manualImportRunning = false;
  $("#manualImportStatus").dataset.state = "error";
  $("#manualImportMessage").textContent = message;
  $("#manualImportTime").textContent = "";
  const button = $("#runManualImport");
  button.disabled = false;
  button.textContent = "Thử kết nối lại";
}

function scheduleManualImportPoll() {
  clearTimeout(manualImportTimer);
  manualImportTimer = setTimeout(() => loadManualImportStatus(true), 2000);
}

async function openImportDetail(id) {
  openDialogLoading("Chi tiết lần import");
  try {
    const item = await api(`/api/import-logs/${encodeURIComponent(id)}`);
    state.activeImportLogId = id;
    state.importChangePage = 1;
    state.importChangeType = "";
    $("#detailEyebrow").textContent = "Lịch sử đồng bộ";
    $("#detailTitle").textContent = item.fileName;
    $("#detailMeta").innerHTML = [
      ["Trạng thái", item.status], ["Product", item.product],
      ["Sales Organization", item.salesOrganization], ["Soft Delete", item.softDeleteEnabled ? "Bật" : "Tắt"],
      ["Bắt đầu", formatDateTime(item.startedAt)]
    ].map(([label, value]) => `<div class="meta-item"><span>${label}</span><strong>${display(value)}</strong></div>`).join("");
    const rows = [
      ["Tổng số dòng", item.totalRows], ["Inserted", item.insertedRows], ["Updated", item.updatedRows],
      ["Soft deleted", item.deletedRows], ["Unchanged", item.unchangedRows], ["Error", item.errorRows], ["Hoàn thành", formatDateTime(item.completedAt)],
      ["File hash", item.fileHash], ["Import ID", item.id]
    ];
    $("#detailContent").innerHTML = `<section class="detail-section"><div class="detail-grid">${rows.map(([name, value]) =>
      `<dl class="detail-field"><dt>${escapeHtml(name)}</dt><dd>${display(value)}</dd></dl>`).join("")}</div></section>` +
      (item.errorMessage ? `<div class="error-box">${escapeHtml(item.errorMessage)}</div>` : "") +
      `<section class="change-audit-section" aria-labelledby="changeAuditHeading">
        <header class="change-audit-header">
          <div><p class="section-kicker">Audit dữ liệu</p><h3 id="changeAuditHeading">Chi tiết Insert / Update / Soft Delete</h3></div>
          <label class="change-filter">Loại thay đổi
            <select id="changeTypeFilter">
              <option value="">Tất cả</option><option value="Insert">Insert</option>
              <option value="Update">Update</option><option value="Delete">Soft Delete</option>
            </select>
          </label>
        </header>
        <div id="changeAuditBody" class="change-audit-body"><div class="loading-state compact-loading"><span class="spinner"></span><span>Đang tải audit…</span></div></div>
        <footer id="changeAuditPagination" class="change-pagination" hidden></footer>
      </section>`;
    $("#changeTypeFilter").addEventListener("change", event => {
      state.importChangeType = event.target.value;
      loadImportChanges(1);
    });
    await loadImportChanges(1);
  } catch (error) {
    $("#detailContent").innerHTML = `<div class="error-box">${escapeHtml(error.message)}</div>`;
  }
}

async function loadImportChanges(page) {
  if (!state.activeImportLogId || !$("#changeAuditBody")) return;
  state.importChangePage = page;
  $("#changeAuditBody").innerHTML = '<div class="loading-state compact-loading"><span class="spinner"></span><span>Đang tải audit…</span></div>';
  const params = new URLSearchParams({ page, pageSize: 10 });
  if (state.importChangeType) params.set("changeType", state.importChangeType);

  try {
    const result = await api(`/api/import-logs/${encodeURIComponent(state.activeImportLogId)}/changes?${params}`);
    if (!result.items.length) {
      $("#changeAuditBody").innerHTML = `<div class="audit-empty"><strong>Không có chi tiết thay đổi</strong><span>${state.importChangeType ? "Không có sự kiện thuộc loại đã chọn." : "Lần import cũ hoặc lần chạy này không phát sinh Insert, Update hay Soft Delete."}</span></div>`;
    } else {
      $("#changeAuditBody").innerHTML = result.items.map(renderChangeAuditItem).join("");
    }

    const pagination = $("#changeAuditPagination");
    pagination.hidden = result.totalPages <= 1;
    pagination.innerHTML = result.totalPages <= 1 ? "" : `
      <span>${formatNumber(result.totalItems)} thay đổi · Trang ${result.page}/${result.totalPages}</span>
      <div><button class="page-button" type="button" data-change-page="${result.page - 1}" ${result.page <= 1 ? "disabled" : ""}>←</button>
      <button class="page-button" type="button" data-change-page="${result.page + 1}" ${result.page >= result.totalPages ? "disabled" : ""}>→</button></div>`;
    pagination.querySelectorAll("[data-change-page]").forEach(button => button.addEventListener("click", () => {
      if (!button.disabled) loadImportChanges(Number(button.dataset.changePage));
    }));
  } catch (error) {
    $("#changeAuditBody").innerHTML = `<div class="error-box">${escapeHtml(error.message)}</div>`;
  }
}

function renderChangeAuditItem(item) {
  const typeLabel = item.changeType === "Delete" ? "Soft Delete" : item.changeType;
  const fields = item.fields.length ? item.fields.map(field => `
    <div class="change-field-row">
      <strong title="${escapeHtml(field.field)}">${escapeHtml(field.field)}</strong>
      <span class="change-old">${display(field.oldValue)}</span>
      <span class="change-arrow" aria-hidden="true">→</span>
      <span class="change-new">${display(field.newValue)}</span>
    </div>`).join("") : '<div class="audit-empty compact"><span>Không có giá trị nghiệp vụ khác nhau.</span></div>';
  const opened = item.changeType === "Update" || item.changeType === "Delete" ? " open" : "";
  return `<article class="change-card" data-change-type="${escapeHtml(item.changeType.toLowerCase())}">
    <header class="change-card-header">
      <div><span class="change-type-pill">${escapeHtml(typeLabel)}</span><strong>SapData ID ${formatNumber(item.sapDataId)}</strong></div>
      <time>${formatDateTime(item.createdAt)}</time>
    </header>
    <div class="change-identity">
      <span><b>SI ID</b>${display(item.shippingInstructionsId)}</span>
      <span><b>Unique Number</b>${display(item.uniqueNumber)}</span>
      <span><b>Dòng Excel</b>${display(item.sourceRowNumber)}</span>
    </div>
    <details${opened}><summary>${formatNumber(item.changedFieldCount)} trường được ghi nhận</summary>
      <div class="change-field-head"><span>Trường</span><span>Giá trị cũ</span><i></i><span>Giá trị mới</span></div>${fields}
    </details>
  </article>`;
}

function openDialogLoading(title) {
  $("#detailEyebrow").textContent = "Đang tải";
  $("#detailTitle").textContent = title;
  $("#detailMeta").innerHTML = "";
  $("#detailContent").innerHTML = '<div class="loading-state"><span class="spinner"></span><span>Đang lấy thông tin chi tiết…</span></div>';
  $("#detailDialog").showModal();
}

function resetFilters() {
  $("#filterForm").reset();
  $("#product").value = "12";
  $("#salesOrganization").value = "SG50";
  $$('#scenarioOptions input').forEach(input => { input.checked = ["PDO", "PWS", "SDS", "SWS"].includes(input.dataset.code || input.value); });
  updateAdvancedCount();
  state.dataPage = 1;
  loadSapData();
}

function updateAdvancedCount() {
  const count = ["salesOffice", "plantCode", "siId", "customer", "oilSc", "oilSo", "oilPo"]
    .filter(id => $("#" + id).value.trim()).length;
  const badge = $("#advancedCount");
  badge.hidden = count === 0;
  badge.textContent = count;
}

function changeDataPage(delta) {
  const next = state.dataPage + delta;
  if (next < 1 || next > state.dataTotalPages) return;
  state.dataPage = next;
  loadSapData();
  $("#resultsHeading").scrollIntoView({ behavior: "smooth", block: "start" });
}

function changeImportPage(delta) {
  const next = state.importPage + delta;
  if (next < 1 || next > state.importTotalPages) return;
  state.importPage = next;
  loadImportLogs();
}

function setLoading(type, active) {
  const loading = type === "data" ? $("#dataLoading") : $("#importsLoading");
  loading.hidden = !active;
  if (type === "data" && active) $("#dataEmpty").hidden = true;
}

function statusClass(value) {
  return String(value || "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
}

function display(value) {
  return value === null || value === undefined || value === "" ? "—" : escapeHtml(String(value));
}

function displayQuantity(value, uom) {
  if (!value) return "—";
  const numeric = Number(value);
  const shown = Number.isFinite(numeric) ? new Intl.NumberFormat("vi-VN", { maximumFractionDigits: 3 }).format(numeric) : escapeHtml(value);
  return `${shown}${uom ? ` <span class="secondary-id">${escapeHtml(uom)}</span>` : ""}`;
}

function formatNumber(value) { return new Intl.NumberFormat("vi-VN").format(Number(value || 0)); }

function formatSapDate(value) {
  if (!value) return "—";
  const match = String(value).match(/^(\d{4})-(\d{2})-(\d{2})/);
  return match ? `${match[3]}/${match[2]}/${match[1]}` : escapeHtml(value);
}

function formatDateTime(value) {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? escapeHtml(String(value)) : new Intl.DateTimeFormat("vi-VN", { dateStyle: "short", timeStyle: "short" }).format(date);
}

function escapeHtml(value) {
  return String(value).replace(/[&<>'"]/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" }[character]));
}

let toastTimer;
function showToast(message) {
  const toast = $("#toast");
  toast.textContent = message;
  toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { toast.hidden = true; }, 5000);
}
