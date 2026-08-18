// ---- 角色邮箱管理 ----

let mailboxSnapshot = null;
let mailboxBusy = false;
let mailboxExpanded = new Set();

function mailboxClaimLabel(flag) {
  if (flag === 1) return '已领取';
  if (flag === 2) return '领取中';
  return '未领取';
}

function mailboxAttachmentSummary(message) {
  const attachments = Array.isArray(message?.attachments) ? message.attachments : [];
  if (attachments.length === 0)
    return '无附件';
  return attachments.map((item) => {
    const name = item.name || ('ID ' + item.itemTemplateId);
    return `${name} x${item.count || 1}`;
  }).join('、');
}

function mailboxStatusLabel(message) {
  const attachments = Array.isArray(message?.attachments) ? message.attachments : [];
  const unclaimed = attachments.filter((item) => Number(item.claimedFlag) === 0).length;
  const parts = [];
  if (message.read) parts.push('已读');
  else parts.push('未读');
  if (message.saved) parts.push('收藏');
  if (unclaimed > 0) parts.push(`未领 ${unclaimed}`);
  if (message.gold > 0 && !message.receivedGold) parts.push('未领金币');
  if (message.shared) parts.push('共享');
  return parts.join(' · ');
}

function updateMailboxButtons() {
  const refresh = $('#btn-refresh-mailbox');
  const clear = $('#btn-clear-character-mailbox');
  if (refresh) refresh.disabled = mailboxBusy || !currentChar;
  if (clear) {
    clear.disabled = mailboxBusy || !currentChar || !mailboxSnapshot || Number(mailboxSnapshot.messageCount) <= 0;
    clear.textContent = mailboxBusy ? '处理中…' : '清空邮箱';
  }
  document.querySelectorAll('[data-mailbox-delete], [data-mailbox-attachment]').forEach((btn) => {
    btn.disabled = mailboxBusy;
  });
}

function renderMailboxEmpty(text) {
  const tbody = $('#mailbox-table tbody');
  if (!tbody) return;
  tbody.innerHTML = `<tr><td colspan="7" class="hint">${escapeHtml(text)}</td></tr>`;
}

function renderMailbox() {
  const summary = $('#mailbox-summary');
  const tbody = $('#mailbox-table tbody');
  if (!tbody) return;
  updateMailboxButtons();
  if (!currentChar) {
    if (summary) summary.textContent = '选择角色后加载邮箱';
    renderMailboxEmpty('请先选择角色');
    return;
  }
  if (!mailboxSnapshot) {
    if (summary) summary.textContent = '正在读取邮箱…';
    renderMailboxEmpty('正在加载…');
    return;
  }

  const messages = Array.isArray(mailboxSnapshot.messages) ? mailboxSnapshot.messages : [];
  if (summary) {
    summary.textContent = `邮件 ${mailboxSnapshot.messageCount || 0} 封 · 未领附件 ${mailboxSnapshot.unclaimedAttachmentCount || 0} · 已领附件 ${mailboxSnapshot.claimedAttachmentCount || 0} · 未领金币 ${(mailboxSnapshot.unclaimedGold || 0).toLocaleString()}`;
  }
  if (messages.length === 0) {
    renderMailboxEmpty('没有邮件');
    return;
  }

  tbody.innerHTML = '';
  for (const message of messages) {
    const messageId = Number(message.messageId);
    const expanded = mailboxExpanded.has(messageId);
    const attachments = Array.isArray(message.attachments) ? message.attachments : [];
    const tr = document.createElement('tr');
    tr.innerHTML =
      `<td><button class="mini" type="button" data-mailbox-toggle="${messageId}">${expanded ? '收起' : '展开'}</button></td>` +
      `<td>${escapeHtml(message.title || '(无标题)')}</td>` +
      `<td>${escapeHtml(message.createdAt || '')}</td>` +
      `<td>${Number(message.gold || 0).toLocaleString()}${message.receivedGold ? '（已领）' : ''}</td>` +
      `<td>${escapeHtml(mailboxAttachmentSummary(message))}</td>` +
      `<td>${escapeHtml(mailboxStatusLabel(message))}</td>` +
      `<td><button class="mini danger" type="button" data-mailbox-delete="${messageId}">删除邮件</button></td>`;
    tbody.appendChild(tr);

    if (!expanded) continue;
    const detail = document.createElement('tr');
    detail.className = 'mailbox-detail-row';
    if (attachments.length === 0) {
      detail.innerHTML = `<td colspan="7" class="hint">这封邮件没有附件。${escapeHtml(message.body || '')}</td>`;
      tbody.appendChild(detail);
      continue;
    }
    const rows = attachments.map((item) => {
      const claimed = Number(item.claimedFlag);
      const canDelete = item.canDelete === true;
      const name = item.name || ('ID ' + item.itemTemplateId);
      const action = canDelete
        ? `<button class="mini danger" type="button" data-mailbox-attachment="${item.attachmentId}">删除物品</button>`
        : '<span class="hint">不可删</span>';
      return `<tr><td>${item.ordinal}</td><td>${item.itemTemplateId}</td><td>${escapeHtml(name)}</td>` +
        `<td>${item.count || 1}</td><td>${escapeHtml(mailboxClaimLabel(claimed))}</td><td>${action}</td></tr>`;
    }).join('');
    detail.innerHTML = `<td colspan="7"><div class="hint">${escapeHtml(message.body || '')}</div>` +
      `<table class="mailbox-attachment-table"><thead><tr><th>#</th><th>ID</th><th>名称</th><th>数量</th><th>领取</th><th></th></tr></thead>` +
      `<tbody>${rows}</tbody></table></td>`;
    tbody.appendChild(detail);
  }

  tbody.querySelectorAll('[data-mailbox-toggle]').forEach((button) => {
    button.onclick = (event) => {
      event.stopPropagation();
      const id = Number(button.getAttribute('data-mailbox-toggle'));
      if (mailboxExpanded.has(id)) mailboxExpanded.delete(id);
      else mailboxExpanded.add(id);
      renderMailbox();
    };
  });
  tbody.querySelectorAll('[data-mailbox-delete]').forEach((button) => {
    button.onclick = (event) => {
      event.stopPropagation();
      deleteCharacterMail(Number(button.getAttribute('data-mailbox-delete')));
    };
  });
  tbody.querySelectorAll('[data-mailbox-attachment]').forEach((button) => {
    button.onclick = (event) => {
      event.stopPropagation();
      deleteCharacterMailAttachment(Number(button.getAttribute('data-mailbox-attachment')));
    };
  });
  updateMailboxButtons();
}

async function loadCharacterMailbox(expectedEpoch = selectEpoch, expectedCharacterId) {
  if (!currentChar) {
    mailboxSnapshot = null;
    renderMailbox();
    return;
  }
  const characterId = expectedCharacterId || currentChar.characterId;
  mailboxSnapshot = null;
  renderMailbox();
  try {
    const data = await api(`/api/characters/${characterId}/mailbox`);
    if (expectedEpoch !== selectEpoch || !currentChar || currentChar.characterId !== characterId)
      return;
    mailboxSnapshot = data;
    renderMailbox();
  } catch (error) {
    if (expectedEpoch !== selectEpoch || !currentChar || currentChar.characterId !== characterId)
      return;
    mailboxSnapshot = null;
    const summary = $('#mailbox-summary');
    if (summary) summary.textContent = error.message;
    renderMailboxEmpty(error.message);
    toast(error.message, true);
  }
}

async function deleteCharacterMail(messageId) {
  if (!currentChar || mailboxBusy || !messageId) return;
  const message = (mailboxSnapshot?.messages || []).find((item) => Number(item.messageId) === messageId);
  const title = message?.title || ('#' + messageId);
  if (!confirm(`删除邮件「${title}」？未领取附件会一起消失，不会进入背包。`)) return;
  const characterId = currentChar.characterId;
  const epoch = selectEpoch;
  mailboxBusy = true;
  updateMailboxButtons();
  try {
    const result = await post(`/api/characters/${characterId}/mailbox/${messageId}/delete`);
    const attachmentCount = Number(result.attachmentCount || 0);
    toast(`已删除邮件 1 封${attachmentCount ? `、未领附件 ${attachmentCount} 件` : ''}`);
    mailboxBusy = false;
    updateMailboxButtons();
    await loadCharacterMailbox(epoch, characterId);
  } catch (error) {
    toast(error.message, true);
  } finally {
    mailboxBusy = false;
    updateMailboxButtons();
  }
}

async function deleteCharacterMailAttachment(attachmentId) {
  if (!currentChar || mailboxBusy || !attachmentId) return;
  if (!confirm('删除该未领取附件？物品不会进入背包。若这封邮件因此变空，会从当前角色收件箱移除。')) return;
  const characterId = currentChar.characterId;
  const epoch = selectEpoch;
  mailboxBusy = true;
  updateMailboxButtons();
  try {
    const result = await post(`/api/characters/${characterId}/mailbox/attachments/${attachmentId}/delete`);
    const mailHint = result.mailRemoved ? '，并移除了空邮件' : '';
    toast(`已删除附件 1 件${mailHint}`);
    mailboxBusy = false;
    updateMailboxButtons();
    await loadCharacterMailbox(epoch, characterId);
  } catch (error) {
    toast(error.message, true);
  } finally {
    mailboxBusy = false;
    updateMailboxButtons();
  }
}

async function clearCharacterMailbox() {
  if (!currentChar || mailboxBusy) return;
  const name = currentChar.name || ('#' + currentChar.characterId);
  if (!confirm(`清空角色 ${name} 的邮箱？未领取附件会一起消失，不会进入背包。`)) return;
  const characterId = currentChar.characterId;
  const epoch = selectEpoch;
  mailboxBusy = true;
  updateMailboxButtons();
  try {
    const result = await post(`/api/characters/${characterId}/mailbox/clear`);
    toast(`已清空邮箱：邮件 ${result.messageCount || 0} 封、未领附件 ${result.attachmentCount || 0} 件`);
    mailboxExpanded.clear();
    mailboxBusy = false;
    updateMailboxButtons();
    await loadCharacterMailbox(epoch, characterId);
  } catch (error) {
    toast(error.message, true);
  } finally {
    mailboxBusy = false;
    updateMailboxButtons();
  }
}

function bindMailboxPanel() {
  const refresh = $('#btn-refresh-mailbox');
  const clear = $('#btn-clear-character-mailbox');
  if (refresh) refresh.onclick = () => loadCharacterMailbox();
  if (clear) clear.onclick = clearCharacterMailbox;
  renderMailbox();
}
