// KanBeast Client Application
// Idempotent, declarative UI updates with full state refresh

const API_BASE = '/api';

// Application state
let tickets = [];
let settings = null;
let connection = null;
let currentDetailTicketId = null;
let activityLogExpanded = false;
let expandedDescriptions = { tasks: {}, subtasks: {} };

// Allowed status transitions
// Backlog -> Active (start work)
// Active/Failed/Done -> Backlog (cancel/reset)
const ALLOWED_TRANSITIONS = {
    'Backlog': ['Active'],
    'Active': ['Backlog'],
    'Failed': ['Backlog'],
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
        startTimestampUpdater();
    } catch (error) {
        console.error('Initialization error:', error);
        updateConnectionStatus('disconnected', 'Error');
    }
}

// Update relative timestamps every second
function startTimestampUpdater() {
    setInterval(() => {
        document.querySelectorAll('.activity-timestamp').forEach(el => {
            const timestamp = el.dataset.timestamp;
            if (timestamp) {
                el.textContent = formatRelativeTime(timestamp);
            }
        });
    }, 1000);
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
    connection.on('TicketUpdated', (ticket) => {
        console.log('SignalR: TicketUpdated received', ticket.id, ticket.title);
        handleTicketEvent();
    });
    connection.on('TicketCreated', (ticket) => {
        console.log('SignalR: TicketCreated received', ticket.id, ticket.title);
        handleTicketEvent();
    });
    connection.on('TicketDeleted', (ticketId) => {
        console.log('SignalR: TicketDeleted received', ticketId);
        handleTicketEvent();
    });

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

// Toggle activity log visibility
function toggleActivityLog(button) {
    const container = button.parentElement;
    const log = container.querySelector('.activity-log');
    const isCollapsed = log.classList.contains('collapsed');

    if (isCollapsed) {
        log.classList.remove('collapsed');
        button.textContent = button.textContent.replace('▼', '▲');
        activityLogExpanded = true;
    } else {
        log.classList.add('collapsed');
        button.textContent = button.textContent.replace('▲', '▼');
        activityLogExpanded = false;
    }
}

// Data loading
async function loadTickets() {
    const response = await fetch(`${API_BASE}/tickets?_=${Date.now()}`, {
        cache: 'no-store'
    });

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
        'Failed': document.getElementById('failed-container'),
        'Done': document.getElementById('done-container')
    };

    const counts = {
        'Backlog': 0,
        'Active': 0,
        'Failed': 0,
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

    // Statuses: Incomplete=0, InProgress=1, AwaitingReview=2, Complete=3, Rejected=4
    const subtasks = (ticket.tasks || []).flatMap(task => task.subtasks || []);

    // Build pip progress bar HTML (each subtask gets a colored pip)
    let pipProgressHtml = '';
    if (subtasks.length > 0) {
        const pips = subtasks.map(st => {
            let pipClass = 'pip-incomplete';
            if (st.status === 'Complete' || st.status === 3) {
                pipClass = 'pip-complete';
            } else if (st.status === 'InProgress' || st.status === 1) {
                pipClass = 'pip-inprogress';
            } else if (st.status === 'AwaitingReview' || st.status === 2) {
                pipClass = 'pip-review';
            } else if (st.status === 'Rejected' || st.status === 4) {
                pipClass = 'pip-rejected';
            }
            return `<div class="pip ${pipClass}"></div>`;
        }).join('');
        pipProgressHtml = `<div class="pip-progress-bar">${pips}</div>`;
    }

    // Find current task and current subtask (first task/subtask with a non-complete status)
    let currentTaskName = '';
    let currentSubtaskName = '';

    for (let taskIdx = 0; taskIdx < (ticket.tasks || []).length; taskIdx++) {
        const task = ticket.tasks[taskIdx];
        const incompleteSubtask = (task.subtasks || []).find(s => {
            return s.status !== 'Complete' && s.status !== 3;
        });
        if (incompleteSubtask) {
            currentTaskName = task.name || '';
            currentSubtaskName = incompleteSubtask.name || '';
            break;
        }
    }

    // Get last activity log entry and parse it
    let lastLogHtml = '';
    if (ticket.activityLog && ticket.activityLog.length > 0) {
        const lastLog = ticket.activityLog[ticket.activityLog.length - 1];
        const parsed = parseLogEntry(lastLog);

        let logClass = '';
        if (parsed.message.includes('Manager:')) {
            logClass = 'manager';
        } else if (parsed.message.includes('Developer:')) {
            logClass = 'developer';
        } else if (parsed.message.includes('Worker:')) {
            logClass = 'worker';
        }

        lastLogHtml = `
            <div class="ticket-activity-log ${logClass}">
                ${parsed.timestamp ? `<span class="activity-timestamp ${logClass}" data-timestamp="${parsed.timestamp}">${formatRelativeTime(parsed.timestamp)}</span>` : ''}
                <span class="activity-message">${escapeHtml(parsed.message)}</span>
            </div>
        `;
    }

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
            ${ticket.maxCost > 0 ? `<span class="cost-badge-inline">$${(ticket.maxCost - ticket.llmCost).toFixed(2)} left</span>` : (ticket.llmCost > 0 ? `<span class="cost-badge-inline">$${ticket.llmCost.toFixed(4)}</span>` : '')}
            <div class="ticket-title">${escapeHtml(ticket.title)}</div>
            <div class="ticket-id">#${ticket.id}</div>
        </div>
        ${currentTaskName ? `<div class="ticket-current-task">${escapeHtml(currentTaskName)}</div>` : ''}
        ${currentSubtaskName ? `<div class="ticket-current-subtask">${escapeHtml(currentSubtaskName)}</div>` : ''}
        ${lastLogHtml}
        <div class="ticket-footer">
            <div class="ticket-meta">
                <span>📅 ${formatDate(ticket.createdAt)}</span>
                ${ticket.containerName && status === 'Active' ? `<span class="worker-badge">${escapeHtml(ticket.containerName)}</span>` : ''}
            </div>
        </div>
        ${pipProgressHtml}
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
        'Failed': '❌',
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

function formatRelativeTime(dateStr) {
    if (!dateStr) {
        return '';
    }

    const date = new Date(dateStr);
    const now = new Date();
    const diffMs = now - date;
    const diffSeconds = Math.floor(diffMs / 1000);
    const diffMinutes = Math.floor(diffSeconds / 60);
    const diffHours = Math.floor(diffMinutes / 60);
    const diffDays = Math.floor(diffHours / 24);

    if (diffSeconds < 60) {
        return `${diffSeconds} sec ago`;
    }

    if (diffMinutes < 60) {
        return `${diffMinutes} min ago`;
    }

    if (diffHours < 24) {
        return `${diffHours} hr ago`;
    }

    if (diffDays < 7) {
        return `${diffDays} day${diffDays > 1 ? 's' : ''} ago`;
    }

    return date.toLocaleDateString();
}

// Parse activity log entry into timestamp and message
function parseLogEntry(logEntry) {
    if (!logEntry || typeof logEntry !== 'string') {
        return {
            timestamp: null,
            message: logEntry || ''
        };
    }

    const trimmed = logEntry.trim();
    const timestampMatch = trimmed.match(/^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] (.+)$/);
    if (timestampMatch) {
        return {
            timestamp: timestampMatch[1].replace(' ', 'T') + 'Z',
            message: timestampMatch[2]
        };
    }

    // If no timestamp found, log it for debugging (only in console, not visible to user)
    if (trimmed.startsWith('[')) {
        console.debug('Failed to parse timestamp from log entry:', trimmed.substring(0, 50));
    }

    return {
        timestamp: null,
        message: trimmed
    };
}

// Ticket details modal
async function showTicketDetails(ticketId) {
    const wasOpen = currentDetailTicketId === ticketId;
    currentDetailTicketId = ticketId;

    if (!wasOpen) {
        expandedDescriptions = { tasks: {}, subtasks: {} };
    }

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

    // Title will be placed in the detail section below

    // Build task/subtask HTML with status icons
    // Statuses: Incomplete=0, InProgress=1, AwaitingReview=2, Complete=3, Rejected=4
    let tasksHtml = '<div class="empty-state">No tasks yet</div>';

    if (ticket.tasks && ticket.tasks.length > 0) {
        tasksHtml = '<ul class="task-list">';

        ticket.tasks.forEach((task, taskIndex) => {
            const subtasks = task.subtasks || [];
            const completedSubtasks = subtasks.filter(s => s.status === 'Complete' || s.status === 3).length;
            const inProgressSubtasks = subtasks.filter(s => 
                s.status === 'InProgress' || s.status === 1 ||
                s.status === 'AwaitingReview' || s.status === 2
            ).length;
            const isComplete = subtasks.length > 0 && completedSubtasks === subtasks.length;
            const isInProgress = inProgressSubtasks > 0 && status === 'Active';

            let taskIcon = '<span class="task-icon task-icon-incomplete">☐</span>';
            if (isComplete) {
                taskIcon = '<span class="task-icon task-icon-complete">✓</span>';
            } else if (isInProgress) {
                taskIcon = '<span class="task-icon task-icon-inprogress"><span class="spinner"></span></span>';
            }

            const taskDescription = task.description || '';
            const hasDescription = taskDescription && taskDescription !== task.name;

            tasksHtml += `
                <li class="task-item${isComplete ? ' completed' : ''}" data-task-index="${taskIndex}" data-has-description="${hasDescription}">
                    ${taskIcon}
                    <div class="task-content">
                        <div class="task-name-row">
                            <div class="task-name">${escapeHtml(task.name || task.description || 'Task')}</div>
                        </div>
                        ${hasDescription ? `<div class="task-description-full" style="display: none;">${escapeHtml(taskDescription)}</div>` : ''}
                        ${subtasks.length > 0 ? `
                            <div class="subtask-list">
                                ${subtasks.map((st, stIndex) => {
                                    const stStatus = st.status;
                                    let subtaskIcon = '<span class="subtask-icon subtask-icon-incomplete">☐</span>';
                                    let subtaskClass = '';

                                    if (stStatus === 'Complete' || stStatus === 3) {
                                        subtaskIcon = '<span class="subtask-icon subtask-icon-complete">✓</span>';
                                        subtaskClass = ' completed';
                                    } else if ((stStatus === 'InProgress' || stStatus === 1) && status === 'Active') {
                                        subtaskIcon = '<span class="subtask-icon subtask-icon-inprogress"><span class="spinner-sm"></span></span>';
                                    } else if (stStatus === 'AwaitingReview' || stStatus === 2) {
                                        subtaskIcon = '<span class="subtask-icon subtask-icon-review">👁</span>';
                                    } else if (stStatus === 'Rejected' || stStatus === 4) {
                                        subtaskIcon = '<span class="subtask-icon subtask-icon-rejected">✗</span>';
                                    }

                                    const stDescription = st.description || '';
                                    const stHasDescription = stDescription && stDescription !== st.name;

                                    return `
                                        <div class="subtask-item${subtaskClass}" data-subtask-index="${stIndex}" data-has-description="${stHasDescription}">
                                            <div class="subtask-name-row">
                                                ${subtaskIcon}
                                                <span class="subtask-name">${escapeHtml(st.name || '')}</span>
                                            </div>
                                            ${stHasDescription ? `<div class="subtask-description-full" style="display: none;">${escapeHtml(stDescription)}</div>` : ''}
                                        </div>
                                    `;
                                }).join('')}
                            </div>
                        ` : ''}
                    </div>
                </li>
            `;
        });

        tasksHtml += '</ul>';
    }

    // Build activity log HTML as expandable
    let activityHtml = '<div class="empty-state">No activity yet</div>';

    if (ticket.activityLog && ticket.activityLog.length > 0) {
        const logCount = ticket.activityLog.length;
        const expandIcon = activityLogExpanded ? '▲' : '▼';
        const collapsedClass = activityLogExpanded ? '' : 'collapsed';

        // Show newest first
        const reversedLogs = [...ticket.activityLog].reverse();
        const latestParsed = parseLogEntry(reversedLogs[0]);

        let latestLogClass = '';
        if (latestParsed.message.includes('Manager:')) {
            latestLogClass = 'manager';
        } else if (latestParsed.message.includes('Developer:')) {
            latestLogClass = 'developer';
        } else if (latestParsed.message.includes('Worker:')) {
            latestLogClass = 'worker';
        }

        activityHtml = `
            <div class="activity-log-container">
                <div class="activity-latest ${latestLogClass}">
                    ${latestParsed.timestamp ? `<span class="activity-timestamp ${latestLogClass}" data-timestamp="${latestParsed.timestamp}">${formatRelativeTime(latestParsed.timestamp)}</span>` : ''}
                    <span class="activity-message">${escapeHtml(latestParsed.message)}</span>
                </div>
                <button class="activity-log-toggle" onclick="toggleActivityLog(this)">
                    ${logCount > 1 ? `Show ${logCount - 1} more` : 'Activity Log'} ${expandIcon}
                </button>
                <div class="activity-log ${collapsedClass}">
        `;

        reversedLogs.slice(1).forEach(log => {
            const parsed = parseLogEntry(log);
            let logClass = '';

            if (parsed.message.includes('Manager:')) {
                logClass = 'manager';
            } else if (parsed.message.includes('Developer:')) {
                logClass = 'developer';
            } else if (parsed.message.includes('Worker:')) {
                logClass = 'worker';
            }

            activityHtml += `<div class="activity-item ${logClass}">`;
            if (parsed.timestamp) {
                activityHtml += `<span class="activity-timestamp ${logClass}" data-timestamp="${parsed.timestamp}">${formatRelativeTime(parsed.timestamp)}</span>`;
            }
            activityHtml += `<span class="activity-message">${escapeHtml(parsed.message)}</span></div>`;
        });

        activityHtml += '</div></div>';
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
            <span class="status-badge ${status.toLowerCase()}">${status}</span>
            ${ticket.branchName ? `<span class="branch-name">🌿 ${escapeHtml(ticket.branchName)}</span>` : '<span class="branch-name-spacer"></span>'}
            <div class="ticket-detail-right">
                <span class="ticket-detail-id">#${ticket.id}</span>
                ${ticket.maxCost > 0 ? `<span class="cost-badge-detail">$${ticket.llmCost.toFixed(2)} spent / $${ticket.maxCost.toFixed(2)} total</span>` : (ticket.llmCost > 0 ? `<span class="cost-badge-detail">$${ticket.llmCost.toFixed(4)} spent</span>` : '')}
            </div>
        </div>

        <div class="detail-section">
            <h2 class="detail-title">${escapeHtml(ticket.title)}</h2>
            <p>${escapeHtml(ticket.description || 'No description provided.')}</p>
        </div>

        <div class="detail-section">
            ${tasksHtml}
        </div>

        <div class="detail-section">
            ${activityHtml}
        </div>

        ${actionsHtml}
    `;

    modal.classList.add('active');

    // Add click handlers for expandable tasks and subtasks with single expansion and state preservation
    detailDiv.querySelectorAll('.task-item[data-has-description="true"]').forEach(taskItem => {
        const taskIndex = taskItem.dataset.taskIndex;
        const descDiv = taskItem.querySelector('.task-description-full');
        const indicator = taskItem.querySelector('.expand-indicator');

        if (expandedDescriptions.tasks[taskIndex]) {
            descDiv.style.display = 'block';
            if (indicator) indicator.textContent = '▼';
        }

        taskItem.addEventListener('click', (e) => {
            if (e.target.closest('.subtask-item')) {
                return;
            }
            const isVisible = descDiv.style.display !== 'none';

            if (!isVisible) {
                Object.keys(expandedDescriptions.tasks).forEach(key => {
                    expandedDescriptions.tasks[key] = false;
                });
                Object.keys(expandedDescriptions.subtasks).forEach(key => {
                    expandedDescriptions.subtasks[key] = false;
                });
                detailDiv.querySelectorAll('.task-description-full, .subtask-description-full').forEach(div => {
                    div.style.display = 'none';
                });
                detailDiv.querySelectorAll('.expand-indicator').forEach(ind => {
                    ind.textContent = 'ⓘ';
                });

                descDiv.style.display = 'block';
                if (indicator) indicator.textContent = '▼';
                expandedDescriptions.tasks[taskIndex] = true;
            } else {
                descDiv.style.display = 'none';
                if (indicator) indicator.textContent = 'ⓘ';
                expandedDescriptions.tasks[taskIndex] = false;
            }
        });
    });

    detailDiv.querySelectorAll('.subtask-item[data-has-description="true"]').forEach(subtaskItem => {
        const taskIndex = subtaskItem.closest('.task-item').dataset.taskIndex;
        const subtaskIndex = subtaskItem.dataset.subtaskIndex;
        const stKey = `${taskIndex}-${subtaskIndex}`;
        const descDiv = subtaskItem.querySelector('.subtask-description-full');
        const indicator = subtaskItem.querySelector('.expand-indicator');

        if (expandedDescriptions.subtasks[stKey]) {
            descDiv.style.display = 'block';
            if (indicator) indicator.textContent = '▼';
        }

        subtaskItem.addEventListener('click', (e) => {
            e.stopPropagation();
            const isVisible = descDiv.style.display !== 'none';

            if (!isVisible) {
                Object.keys(expandedDescriptions.tasks).forEach(key => {
                    expandedDescriptions.tasks[key] = false;
                });
                Object.keys(expandedDescriptions.subtasks).forEach(key => {
                    expandedDescriptions.subtasks[key] = false;
                });
                detailDiv.querySelectorAll('.task-description-full, .subtask-description-full').forEach(div => {
                    div.style.display = 'none';
                });
                detailDiv.querySelectorAll('.expand-indicator').forEach(ind => {
                    ind.textContent = 'ⓘ';
                });

                descDiv.style.display = 'block';
                if (indicator) indicator.textContent = '▼';
                expandedDescriptions.subtasks[stKey] = true;
            } else {
                descDiv.style.display = 'none';
                if (indicator) indicator.textContent = 'ⓘ';
                expandedDescriptions.subtasks[stKey] = false;
            }
        });
    });

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
    // Close modal immediately before API call to prevent SignalR race condition
    if (currentDetailTicketId === ticketId) {
        document.getElementById('ticketDetailModal').classList.remove('active');
        currentDetailTicketId = null;
    }

    try {
        const response = await fetch(`${API_BASE}/tickets/${ticketId}/status`, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ status: newStatus })
        });

        if (response.ok) {
            await refreshAllTickets();
        } else {
            console.error('Failed to move ticket');
        }
    } catch (error) {
        console.error('Error moving ticket:', error);
    }
}

// Make moveTicket available globally for onclick handlers
window.moveTicket = moveTicket;
window.toggleActivityLog = toggleActivityLog;

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
        document.getElementById('gitApiToken').value = settings.gitConfig.apiToken || '';
        document.getElementById('gitPassword').value = settings.gitConfig.password || '';
    }

    // Populate compaction settings
    const managerType = (settings && settings.managerCompaction && settings.managerCompaction.type) || 'disabled';
    const developerType = (settings && settings.developerCompaction && settings.developerCompaction.type) || 'disabled';
    document.getElementById('managerCompactionType').value = managerType === 'summarize' ? 'summarize' : 'disabled';
    document.getElementById('developerCompactionType').value = developerType === 'summarize' ? 'summarize' : 'disabled';
    document.getElementById('managerContextThreshold').value = (settings && settings.managerCompaction && settings.managerCompaction.contextSizeThreshold) || 0;
    document.getElementById('developerContextThreshold').value = (settings && settings.developerCompaction && settings.developerCompaction.contextSizeThreshold) || 0;
    updateCompactionVisibility();

    // Populate worker settings
    document.getElementById('jsonLogging').checked = (settings && settings.jsonLogging) || false;

    // Populate LLM configs
    renderLLMConfigs();

    // Setup accordion handlers
    setupAccordions();

    modal.classList.add('active');
}

function setupAccordions() {
    document.querySelectorAll('.accordion-header').forEach(header => {
        header.removeEventListener('click', toggleAccordion);
        header.addEventListener('click', toggleAccordion);
    });
}

function toggleAccordion(e) {
    const accordion = e.currentTarget.closest('.accordion');
    accordion.classList.toggle('collapsed');
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
        configEl.className = 'llm-config accordion';
        const modelName = config.model || `LLM ${index + 1}`;
        configEl.innerHTML = `
            <div class="accordion-header">
                <span>🧠 ${escapeHtml(modelName)}</span>
                <span class="accordion-icon">▼</span>
            </div>
            <div class="accordion-content">
                <div class="form-group">
                    <label>Model</label>
                    <input type="text" class="llm-model" value="${escapeHtml(config.model || '')}" placeholder="gpt-4o">
                </div>
                <div class="form-group">
                    <label>API Key</label>
                    <input type="password" class="llm-apikey" value="${escapeHtml(config.apiKey || '')}" placeholder="sk-...">
                </div>
                <div class="form-group">
                    <label>Endpoint (optional, for Azure/custom)</label>
                    <input type="text" class="llm-endpoint" value="${escapeHtml(config.endpoint || '')}" placeholder="https://...">
                </div>
                <div class="form-group">
                    <label>Context Length</label>
                    <input type="number" class="llm-context-length" value="${config.contextLength || 128000}" min="1000" step="1000" placeholder="128000">
                </div>
                <button type="button" class="btn-danger btn-sm" data-index="${index}" style="width: 100%;">Remove This LLM</button>
            </div>
        `;

        configEl.querySelector('.btn-danger').addEventListener('click', () => removeLLMConfig(index));
        configEl.querySelector('.accordion-header').addEventListener('click', toggleAccordion);
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
    if (e) {
        e.preventDefault();
    }
    await saveSettings();
}

async function saveSettings() {
    const updatedSettings = {
        llmConfigs: collectLLMConfigs(),
        gitConfig: {
            repositoryUrl: document.getElementById('gitUrl').value,
            username: document.getElementById('gitUsername').value,
            email: document.getElementById('gitEmail').value,
            sshKey: document.getElementById('gitSshKey').value || null,
            apiToken: document.getElementById('gitApiToken').value || null,
            password: document.getElementById('gitPassword').value || null
        },
        managerCompaction: {
            type: document.getElementById('managerCompactionType').value,
            contextSizeThreshold: parseInt(document.getElementById('managerContextThreshold').value, 10) || 0
        },
        developerCompaction: {
            type: document.getElementById('developerCompactionType').value,
            contextSizeThreshold: parseInt(document.getElementById('developerContextThreshold').value, 10) || 0
        },
        jsonLogging: document.getElementById('jsonLogging').checked
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

// Make saveSettings available globally for onclick
window.saveSettings = saveSettings;

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
