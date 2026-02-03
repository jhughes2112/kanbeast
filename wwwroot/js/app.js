// KanBeast Client Application
// Idempotent, declarative UI updates with full state refresh

const API_BASE = '/api';

// Application state
let tickets = [];
let settings = null;
let connection = null;
let currentDetailTicketId = null;

// Allowed status transitions
// Backlog -> Active (start work)
// Active/Testing/Done -> Backlog (cancel/reset)
const ALLOWED_TRANSITIONS = {
    'Backlog': ['Active'],
    'Active': ['Backlog'],
    'Testing': ['Backlog'],
    'Done': ['Backlog']
};

// Initialize application
async function init() {
    try {
        await loadTickets();
        await loadSettings();
        renderAllTickets();
        setupSignalR();
        setupEventListeners();
        setupDragAndDrop();
    } catch (error) {
        console.error('Initialization error:', error);
        updateConnectionStatus('disconnected', 'Error');
    }
}

// SignalR connection management
function setupSignalR() {
    updateConnectionStatus('connecting', 'Connecting...');

    connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/kanban')
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();

    // All events trigger full state refresh for idempotency
    connection.on('TicketUpdated', handleTicketEvent);
    connection.on('TicketCreated', handleTicketEvent);
    connection.on('TicketDeleted', handleTicketEvent);

    connection.onreconnecting(() => {
        updateConnectionStatus('connecting', 'Reconnecting...');
    });

    connection.onreconnected(() => {
        updateConnectionStatus('connected', 'Connected');
        refreshAllTickets();
    });

    connection.onclose(() => {
        updateConnectionStatus('disconnected', 'Disconnected');
    });

    startConnection();
}

async function startConnection() {
    try {
        await connection.start();
        updateConnectionStatus('connected', 'Connected');
    } catch (error) {
        console.error('SignalR connection failed:', error);
        updateConnectionStatus('disconnected', 'Connection Failed');
        setTimeout(startConnection, 5000);
    }
}

function updateConnectionStatus(status, text) {
    const statusEl = document.getElementById('connectionStatus');
    const textEl = document.getElementById('connectionText');

    if (statusEl && textEl) {
        statusEl.className = status;
        textEl.textContent = text;
    }
}

// Event handlers - always refresh full state
async function handleTicketEvent() {
    await refreshAllTickets();

    // If viewing a ticket detail, refresh it too
    if (currentDetailTicketId) {
        await showTicketDetails(currentDetailTicketId);
    }
}

async function refreshAllTickets() {
    await loadTickets();
    renderAllTickets();
}

// Data loading
async function loadTickets() {
    const response = await fetch(`${API_BASE}/tickets`);

    if (!response.ok) {
        throw new Error(`Failed to load tickets: ${response.status}`);
    }

    tickets = await response.json();
}

async function loadSettings() {
    try {
        const response = await fetch(`${API_BASE}/settings`);

        if (response.ok) {
            settings = await response.json();
        }
    } catch (error) {
        console.warn('Could not load settings:', error);
        settings = null;
    }
}

// Rendering - full declarative render
function renderAllTickets() {
    const containers = {
        'Backlog': document.getElementById('backlog-container'),
        'Active': document.getElementById('active-container'),
        'Testing': document.getElementById('testing-container'),
        'Done': document.getElementById('done-container')
    };

    const counts = {
        'Backlog': 0,
        'Active': 0,
        'Testing': 0,
        'Done': 0
    };

    // Clear all containers
    Object.values(containers).forEach(container => {
        if (container) {
            container.innerHTML = '';
        }
    });

    // Sort tickets by creation date (newest first for backlog, oldest first for others)
    const sortedTickets = [...tickets].sort((a, b) => {
        if (a.status === 'Backlog') {
            return new Date(b.createdAt) - new Date(a.createdAt);
        }

        return new Date(a.createdAt) - new Date(b.createdAt);
    });

    // Render each ticket
    sortedTickets.forEach(ticket => {
        const status = ticket.status || 'Backlog';
        const container = containers[status];

        if (container) {
            container.appendChild(createTicketElement(ticket));
            counts[status]++;
        }
    });

    // Update counts
    Object.keys(counts).forEach(status => {
        const countEl = document.getElementById(`${status.toLowerCase()}-count`);

        if (countEl) {
            countEl.textContent = counts[status];
        }
    });

    // Show empty states
    Object.entries(containers).forEach(([status, container]) => {
        if (container && container.children.length === 0) {
            container.innerHTML = `
                <div class="empty-state">
                    <div class="empty-state-icon">${getStatusIcon(status)}</div>
                    <div>No tickets</div>
                </div>
            `;
        }
    });
}

function createTicketElement(ticket) {
    const status = ticket.status || 'Backlog';
    const isDraggable = canMoveFrom(status);

    const subtasks = (ticket.tasks || []).flatMap(task => task.subtasks || []);
    const completedCount = subtasks.filter(s => s.status === 'Complete' || s.status === 1).length;
    const totalCount = subtasks.length;
    const progressPercent = totalCount > 0 ? Math.round((completedCount / totalCount) * 100) : 0;

    const ticketEl = document.createElement('div');
    ticketEl.className = `ticket${isDraggable ? ' draggable' : ''}`;
    ticketEl.dataset.ticketId = ticket.id;
    ticketEl.dataset.status = status;

    if (isDraggable) {
        ticketEl.draggable = true;
    }

    ticketEl.innerHTML = `
        <div class="ticket-status-indicator"></div>
        <div class="ticket-header">
            <div class="ticket-title">${escapeHtml(ticket.title)}</div>
            <div class="ticket-id">#${ticket.id}</div>
        </div>
        <div class="ticket-description">${escapeHtml(ticket.description || '')}</div>
        <div class="ticket-footer">
            <div class="ticket-meta">
                <span>📅 ${formatDate(ticket.createdAt)}</span>
                ${ticket.workerId ? '<span class="worker-badge">🤖 AI Working</span>' : ''}
            </div>
            ${totalCount > 0 ? `
                <div class="ticket-progress">
                    <div class="progress-bar">
                        <div class="progress-bar-fill${progressPercent === 100 ? ' complete' : ''}" 
                             style="width: ${progressPercent}%"></div>
                    </div>
                    <span class="progress-text">${completedCount}/${totalCount}</span>
                </div>
            ` : ''}
        </div>
    `;

    ticketEl.addEventListener('click', (e) => {
        if (!ticketEl.classList.contains('dragging')) {
            showTicketDetails(ticket.id);
        }
    });

    return ticketEl;
}

function getStatusIcon(status) {
    const icons = {
        'Backlog': '📋',
        'Active': '🚀',
        'Testing': '🧪',
        'Done': '✅'
    };

    return icons[status] || '📋';
}

function formatDate(dateStr) {
    if (!dateStr) {
        return '';
    }

    const date = new Date(dateStr);
    const now = new Date();
    const diffMs = now - date;
    const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));

    if (diffDays === 0) {
        return 'Today';
    }

    if (diffDays === 1) {
        return 'Yesterday';
    }

    if (diffDays < 7) {
        return `${diffDays} days ago`;
    }

    return date.toLocaleDateString();
}

// Ticket details modal
async function showTicketDetails(ticketId) {
    currentDetailTicketId = ticketId;

    const response = await fetch(`${API_BASE}/tickets/${ticketId}`);

    if (!response.ok) {
        console.error('Failed to load ticket details');
        return;
    }

    const ticket = await response.json();
    const modal = document.getElementById('ticketDetailModal');
    const titleEl = document.getElementById('detailTitle');
    const detailDiv = document.getElementById('ticketDetail');
    const status = ticket.status || 'Backlog';

    titleEl.textContent = ticket.title;

    // Build task/subtask HTML
    let tasksHtml = '<div class="empty-state">No tasks yet</div>';

    if (ticket.tasks && ticket.tasks.length > 0) {
        tasksHtml = '<ul class="task-list">';

        ticket.tasks.forEach(task => {
            const subtasks = task.subtasks || [];
            const completedSubtasks = subtasks.filter(s => s.status === 'Complete' || s.status === 1).length;
            const isComplete = subtasks.length > 0 && completedSubtasks === subtasks.length;

            tasksHtml += `
                <li class="task-item${isComplete ? ' completed' : ''}">
                    <input type="checkbox" class="task-checkbox" ${isComplete ? 'checked' : ''} disabled>
                    <div class="task-content">
                        <div class="task-name">${escapeHtml(task.name || task.description || 'Task')}</div>
                        ${subtasks.length > 0 ? `
                            <div class="subtask-list">
                                ${subtasks.map(st => `
                                    <div class="subtask-item${(st.status === 'Complete' || st.status === 1) ? ' completed' : ''}">
                                        ${(st.status === 'Complete' || st.status === 1) ? '✓' : '○'} ${escapeHtml(st.name || '')}
                                    </div>
                                `).join('')}
                            </div>
                        ` : ''}
                    </div>
                </li>
            `;
        });

        tasksHtml += '</ul>';
    }

    // Build activity log HTML
    let activityHtml = '<div class="empty-state">No activity yet</div>';

    if (ticket.activityLog && ticket.activityLog.length > 0) {
        activityHtml = '<div class="activity-log">';
        // Show newest first
        const reversedLogs = [...ticket.activityLog].reverse();

        reversedLogs.forEach(log => {
            let logClass = '';

            if (log.includes('Manager:')) {
                logClass = 'manager';
            } else if (log.includes('Developer:')) {
                logClass = 'developer';
            } else if (log.includes('Worker:')) {
                logClass = 'worker';
            }

            activityHtml += `<div class="activity-item ${logClass}">${escapeHtml(log)}</div>`;
        });

        activityHtml += '</div>';
    }

    // Build action buttons based on allowed transitions
    let actionsHtml = '<div class="ticket-actions">';
    const allowedTargets = ALLOWED_TRANSITIONS[status] || [];
    const canDelete = status === 'Backlog' || status === 'Done';

    if (status === 'Backlog') {
        actionsHtml += `<button class="btn-primary" onclick="moveTicket('${ticketId}', 'Active')">🚀 Start Work</button>`;
    } else if (status !== 'Done') {
        actionsHtml += `<button class="btn-danger" onclick="moveTicket('${ticketId}', 'Backlog')">↩️ Cancel & Return to Backlog</button>`;
    } else {
        actionsHtml += `<button class="btn-secondary" onclick="moveTicket('${ticketId}', 'Backlog')">↩️ Reopen</button>`;
    }

    if (canDelete) {
        actionsHtml += `<button class="btn-danger btn-delete" onclick="deleteTicket('${ticketId}')">🗑️ Delete</button>`;
    }

    actionsHtml += '</div>';

    detailDiv.innerHTML = `
        <div class="ticket-detail-header">
            <span class="ticket-detail-id">#${ticket.id}</span>
            <span class="status-badge ${status.toLowerCase()}">${status}</span>
            ${ticket.branchName ? `<span style="color: var(--gray-500); font-size: 0.875rem;">🌿 ${escapeHtml(ticket.branchName)}</span>` : ''}
        </div>

        <div class="detail-section">
            <h3>Description</h3>
            <p>${escapeHtml(ticket.description || 'No description provided.')}</p>
        </div>

        <div class="detail-section">
            <h3>Tasks</h3>
            ${tasksHtml}
        </div>

        <div class="detail-section">
            <h3>Activity Log</h3>
            ${activityHtml}
        </div>

        ${actionsHtml}
    `;

    modal.classList.add('active');

    // Subscribe to updates for this ticket
    if (connection && connection.state === signalR.HubConnectionState.Connected) {
        try {
            await connection.invoke('SubscribeToTicket', ticketId);
        } catch (error) {
            console.warn('Could not subscribe to ticket updates:', error);
        }
    }
}

// Move ticket to new status
async function moveTicket(ticketId, newStatus) {
    try {
        const response = await fetch(`${API_BASE}/tickets/${ticketId}/status`, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ status: newStatus })
        });

        if (response.ok) {
            await refreshAllTickets();

            // Close detail modal if open
            if (currentDetailTicketId === ticketId) {
                document.getElementById('ticketDetailModal').classList.remove('active');
                currentDetailTicketId = null;
            }
        } else {
            console.error('Failed to move ticket');
        }
    } catch (error) {
        console.error('Error moving ticket:', error);
    }
}

// Make moveTicket available globally for onclick handlers
window.moveTicket = moveTicket;

// Delete ticket
async function deleteTicket(ticketId) {
    if (!confirm('Are you sure you want to delete this ticket? This cannot be undone.')) {
        return;
    }

    try {
        const response = await fetch(`${API_BASE}/tickets/${ticketId}`, {
            method: 'DELETE'
        });

        if (response.ok) {
            await refreshAllTickets();

            // Close detail modal
            document.getElementById('ticketDetailModal').classList.remove('active');
            currentDetailTicketId = null;
        } else {
            console.error('Failed to delete ticket');
        }
    } catch (error) {
        console.error('Error deleting ticket:', error);
    }
}

// Make deleteTicket available globally for onclick handlers
window.deleteTicket = deleteTicket;

// Drag and drop
function canMoveFrom(status) {
    return ALLOWED_TRANSITIONS[status] && ALLOWED_TRANSITIONS[status].length > 0;
}

function canMoveTo(fromStatus, toStatus) {
    const allowed = ALLOWED_TRANSITIONS[fromStatus] || [];

    return allowed.includes(toStatus);
}

function setupDragAndDrop() {
    document.querySelectorAll('.column').forEach(column => {
        column.addEventListener('dragover', handleDragOver);
        column.addEventListener('dragenter', handleDragEnter);
        column.addEventListener('dragleave', handleDragLeave);
        column.addEventListener('drop', handleDrop);
    });

    document.addEventListener('dragstart', handleDragStart);
    document.addEventListener('dragend', handleDragEnd);
}

let draggedTicketId = null;
let draggedFromStatus = null;

function handleDragStart(e) {
    if (!e.target.classList.contains('ticket') || !e.target.classList.contains('draggable')) {
        return;
    }

    draggedTicketId = e.target.dataset.ticketId;
    draggedFromStatus = e.target.dataset.status;

    e.target.classList.add('dragging');
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', draggedTicketId);

    // Highlight valid drop targets
    document.querySelectorAll('.column').forEach(column => {
        const targetStatus = column.dataset.status;

        if (!canMoveTo(draggedFromStatus, targetStatus)) {
            column.classList.add('drop-disabled');
        }
    });
}

function handleDragEnd(e) {
    if (e.target.classList.contains('ticket')) {
        e.target.classList.remove('dragging');
    }

    document.querySelectorAll('.column').forEach(column => {
        column.classList.remove('drag-over', 'drop-disabled');
    });

    draggedTicketId = null;
    draggedFromStatus = null;
}

function handleDragEnter(e) {
    e.preventDefault();
    const column = e.currentTarget;
    const targetStatus = column.dataset.status;

    if (draggedFromStatus && canMoveTo(draggedFromStatus, targetStatus)) {
        column.classList.add('drag-over');
    }
}

function handleDragLeave(e) {
    const column = e.currentTarget;

    if (!column.contains(e.relatedTarget)) {
        column.classList.remove('drag-over');
    }
}

function handleDragOver(e) {
    e.preventDefault();
    const column = e.currentTarget;
    const targetStatus = column.dataset.status;

    if (draggedFromStatus && canMoveTo(draggedFromStatus, targetStatus)) {
        e.dataTransfer.dropEffect = 'move';
    } else {
        e.dataTransfer.dropEffect = 'none';
    }
}

async function handleDrop(e) {
    e.preventDefault();
    e.stopPropagation();

    const column = e.currentTarget;
    column.classList.remove('drag-over');

    const ticketId = e.dataTransfer.getData('text/plain');
    const targetStatus = column.dataset.status;

    if (!ticketId || !draggedFromStatus) {
        return;
    }

    if (!canMoveTo(draggedFromStatus, targetStatus)) {
        return;
    }

    await moveTicket(ticketId, targetStatus);
}

// Event listeners
function setupEventListeners() {
    // New ticket button
    document.getElementById('newTicketBtn').addEventListener('click', () => {
        document.getElementById('newTicketModal').classList.add('active');
        document.getElementById('ticketTitle').focus();
    });

    // Settings button
    document.getElementById('settingsBtn').addEventListener('click', showSettings);

    // New ticket form
    document.getElementById('newTicketForm').addEventListener('submit', handleCreateTicket);

    // Git config form
    document.getElementById('gitConfigForm').addEventListener('submit', handleSaveSettings);

    // Add LLM button
    document.getElementById('addLLMBtn').addEventListener('click', addLLMConfig);

    // Compaction type dropdowns
    document.getElementById('managerCompactionType').addEventListener('change', updateCompactionVisibility);
    document.getElementById('developerCompactionType').addEventListener('change', updateCompactionVisibility);

    // Close modal buttons
    document.querySelectorAll('.close').forEach(btn => {
        btn.addEventListener('click', () => {
            const modal = btn.closest('.modal');
            modal.classList.remove('active');

            if (modal.id === 'ticketDetailModal') {
                currentDetailTicketId = null;
            }
        });
    });

    // Close modals on backdrop click
    document.querySelectorAll('.modal').forEach(modal => {
        modal.addEventListener('click', (e) => {
            if (e.target === modal) {
                modal.classList.remove('active');

                if (modal.id === 'ticketDetailModal') {
                    currentDetailTicketId = null;
                }
            }
        });
    });

    // Close modals on Escape key
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            document.querySelectorAll('.modal.active').forEach(modal => {
                modal.classList.remove('active');

                if (modal.id === 'ticketDetailModal') {
                    currentDetailTicketId = null;
                }
            });
        }
    });
}

async function handleCreateTicket(e) {
    e.preventDefault();

    const title = document.getElementById('ticketTitle').value.trim();
    const description = document.getElementById('ticketDescription').value.trim();

    if (!title) {
        return;
    }

    try {
        const response = await fetch(`${API_BASE}/tickets`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                title,
                description,
                status: 'Backlog'
            })
        });

        if (response.ok) {
            document.getElementById('newTicketModal').classList.remove('active');
            document.getElementById('newTicketForm').reset();
            await refreshAllTickets();
        } else {
            console.error('Failed to create ticket');
        }
    } catch (error) {
        console.error('Error creating ticket:', error);
    }
}

// Settings management
function showSettings() {
    const modal = document.getElementById('settingsModal');

    // Populate Git config
    if (settings && settings.gitConfig) {
        document.getElementById('gitUrl').value = settings.gitConfig.repositoryUrl || '';
        document.getElementById('gitUsername').value = settings.gitConfig.username || '';
        document.getElementById('gitEmail').value = settings.gitConfig.email || '';
        document.getElementById('gitSshKey').value = settings.gitConfig.sshKey || '';
    }

    // Populate compaction settings
    const managerType = (settings && settings.managerCompaction && settings.managerCompaction.type) || 'disabled';
    const developerType = (settings && settings.developerCompaction && settings.developerCompaction.type) || 'disabled';
    document.getElementById('managerCompactionType').value = managerType === 'summarize' ? 'summarize' : 'disabled';
    document.getElementById('developerCompactionType').value = developerType === 'summarize' ? 'summarize' : 'disabled';
    document.getElementById('managerContextThreshold').value = (settings && settings.managerCompaction && settings.managerCompaction.contextSizeThreshold) || 0;
    document.getElementById('developerContextThreshold').value = (settings && settings.developerCompaction && settings.developerCompaction.contextSizeThreshold) || 0;
    updateCompactionVisibility();

    // Populate LLM configs
    renderLLMConfigs();

    modal.classList.add('active');
}

function updateCompactionVisibility() {
    const managerType = document.getElementById('managerCompactionType').value;
    const developerType = document.getElementById('developerCompactionType').value;
    document.getElementById('managerCompactionOptions').style.display = managerType === 'summarize' ? 'block' : 'none';
    document.getElementById('developerCompactionOptions').style.display = developerType === 'summarize' ? 'block' : 'none';
}

function renderLLMConfigs() {
    const container = document.getElementById('llmConfigs');
    const llmConfigs = (settings && settings.llmConfigs) || [];

    container.innerHTML = '';

    if (llmConfigs.length === 0) {
        container.innerHTML = '<div class="empty-state">No LLM configurations</div>';
        return;
    }

    llmConfigs.forEach((config, index) => {
        const configEl = document.createElement('div');
        configEl.className = 'llm-config';
        configEl.innerHTML = `
            <button type="button" class="btn-danger btn-sm remove-btn" data-index="${index}">Remove</button>
            <div class="form-group">
                <label>Model</label>
                <input type="text" class="llm-model" value="${escapeHtml(config.model || '')}" placeholder="gpt-4o">
            </div>
            <div class="form-group">
                <label>API Key</label>
                <input type="password" class="llm-apikey" value="${escapeHtml(config.apiKey || '')}" placeholder="sk-...">
            </div>
            <div class="form-group">
                <label>Endpoint (optional, for Azure)</label>
                <input type="text" class="llm-endpoint" value="${escapeHtml(config.endpoint || '')}" placeholder="https://...">
            </div>
            <div class="form-group">
                <label>Context Length</label>
                <input type="number" class="llm-context-length" value="${config.contextLength || 128000}" min="1000" step="1000" placeholder="128000">
            </div>
        `;

        configEl.querySelector('.remove-btn').addEventListener('click', () => removeLLMConfig(index));
        container.appendChild(configEl);
    });
}

function addLLMConfig() {
    if (!settings) {
        settings = { llmConfigs: [], gitConfig: {} };
    }

    if (!settings.llmConfigs) {
        settings.llmConfigs = [];
    }

    settings.llmConfigs.push({ model: '', apiKey: '', endpoint: '', contextLength: 128000 });
    renderLLMConfigs();
}

function removeLLMConfig(index) {
    if (settings && settings.llmConfigs) {
        settings.llmConfigs.splice(index, 1);
        renderLLMConfigs();
    }
}

function collectLLMConfigs() {
    const configs = [];

    document.querySelectorAll('.llm-config').forEach(configEl => {
        configs.push({
            model: configEl.querySelector('.llm-model').value,
            apiKey: configEl.querySelector('.llm-apikey').value,
            endpoint: configEl.querySelector('.llm-endpoint').value || null,
            contextLength: parseInt(configEl.querySelector('.llm-context-length').value, 10) || 128000
        });
    });

    return configs;
}

async function handleSaveSettings(e) {
    e.preventDefault();

    const updatedSettings = {
        llmConfigs: collectLLMConfigs(),
        gitConfig: {
            repositoryUrl: document.getElementById('gitUrl').value,
            username: document.getElementById('gitUsername').value,
            email: document.getElementById('gitEmail').value,
            sshKey: document.getElementById('gitSshKey').value || null
        },
        managerCompaction: {
            type: document.getElementById('managerCompactionType').value,
            contextSizeThreshold: parseInt(document.getElementById('managerContextThreshold').value, 10) || 0
        },
        developerCompaction: {
            type: document.getElementById('developerCompactionType').value,
            contextSizeThreshold: parseInt(document.getElementById('developerContextThreshold').value, 10) || 0
        }
    };

    try {
        const response = await fetch(`${API_BASE}/settings`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(updatedSettings)
        });

        if (response.ok) {
            settings = await response.json();
            document.getElementById('settingsModal').classList.remove('active');
        } else {
            console.error('Failed to save settings');
            alert('Failed to save settings. Please check the console for details.');
        }
    } catch (error) {
        console.error('Error saving settings:', error);
        alert('Error saving settings. Please check the console for details.');
    }
}

// Utility functions
function escapeHtml(text) {
    if (text == null) {
        return '';
    }

    const div = document.createElement('div');
    div.textContent = String(text);

    return div.innerHTML;
}

// Initialize on DOM ready
document.addEventListener('DOMContentLoaded', init);
