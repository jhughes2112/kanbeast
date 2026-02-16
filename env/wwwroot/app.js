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

// Audio context for notification sounds (created lazily after user interaction).
let audioCtx = null;

function ensureAudioContext() {
    if (!audioCtx) {
        audioCtx = new (window.AudioContext || window.webkitAudioContext)();
    }
    return audioCtx;
}

function playTone(frequency, durationMs) {
    const ctx = ensureAudioContext();
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = 'sine';
    osc.frequency.value = frequency;
    gain.gain.value = 0.12;
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + durationMs / 1000);
    osc.connect(gain);
    gain.connect(ctx.destination);
    osc.start();
    osc.stop(ctx.currentTime + durationMs / 1000);
}

function playDing() { playTone(880, 150); }
function playDong() { playTone(523, 250); }

// Counts completed subtasks and fully-complete tasks from a ticket object.
function countCompletions(ticket) {
    let subtasks = 0;
    let tasks = 0;
    if (!ticket.tasks) return { subtasks, tasks };
    for (let t = 0; t < ticket.tasks.length; t++) {
        const task = ticket.tasks[t];
        const subs = task.subtasks || [];
        if (subs.length === 0) continue;
        let allDone = true;
        for (let s = 0; s < subs.length; s++) {
            if (subs[s].status === 'Complete' || subs[s].status === 3) {
                subtasks++;
            } else {
                allDone = false;
            }
        }
        if (allDone) tasks++;
    }
    return { subtasks, tasks };
}

// Checks a ticket update for new completions and plays sounds.
function checkCompletionSounds(updatedTicket) {
    const old = tickets.find(t => t.id === updatedTicket.id);
    if (!old) return;
    const before = countCompletions(old);
    const after = countCompletions(updatedTicket);
    if (after.tasks > before.tasks) {
        playDong();
    } else if (after.subtasks > before.subtasks) {
        playDing();
    }
}

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
    let retryCount = 0;
    const maxRetries = 20;
    const baseDelay = 1000;

    while (retryCount < maxRetries) {
        try {
            if (retryCount > 0) {
                updateConnectionStatus('connecting', `Waiting for server... (attempt ${retryCount + 1})`);
            }

            await loadTickets();
            await loadSettings();
            renderAllTickets();
            setupSignalR();
            setupEventListeners();
            setupDragAndDrop();
            startTimestampUpdater();
            return;
        } catch (error) {
            retryCount++;
            console.warn(`Initialization attempt ${retryCount} failed:`, error);

            if (retryCount >= maxRetries) {
                console.error('Initialization failed after maximum retries:', error);
                updateConnectionStatus('disconnected', 'Server unavailable');
                return;
            }

            const delay = Math.min(baseDelay * Math.pow(1.5, retryCount - 1), 10000);
            await new Promise(resolve => setTimeout(resolve, delay));
        }
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
        checkCompletionSounds(ticket);
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

    // Conversation events from workers.
    connection.on('ConversationsUpdated', (ticketId, conversations) => {
        ticketConversations[ticketId] = conversations;
        // If viewing this ticket's detail, update both dropdowns.
        if (currentDetailTicketId === ticketId) {
            updateConversationDropdown(ticketId);
            updateDetailConversationDropdown(ticketId);
        }
    });

    connection.on('ConversationMessagesAppended', (ticketId, conversationId, messages) => {
        const key = `${ticketId}:${conversationId}`;
        if (!conversationMessages[key]) {
            conversationMessages[key] = [];
        }
        const startIndex = conversationMessages[key].length;
        for (let i = 0; i < messages.length; i++) {
            conversationMessages[key].push(messages[i]);
        }
        // If this conversation is currently displayed in the full chat modal, render new messages live.
        if (chatModalTicketId === ticketId && chatModalConversationId === conversationId) {
            const fullMessages = conversationMessages[key];
            for (let i = 0; i < messages.length; i++) {
                renderSingleChatMessage(fullMessages, startIndex + i);
            }
        }
        // Also render in the inline detail chat if visible.
        if (detailChatTicketId === ticketId && detailChatConversationId === conversationId) {
            const messagesDiv = document.getElementById('detailChatMessages');
            if (messagesDiv) {
                const atBottomBefore = messagesDiv.scrollHeight - messagesDiv.scrollTop - messagesDiv.clientHeight < 40;
                const fullMessages = conversationMessages[key];
                for (let i = 0; i < messages.length; i++) {
                    renderDetailChatMessage(fullMessages, startIndex + i, messagesDiv);
                }
                // If was at bottom, scroll to bottom and save state.
                if (atBottomBefore) {
                    requestAnimationFrame(() => {
                        messagesDiv.scrollTop = messagesDiv.scrollHeight;
                        saveTicketConversationState(ticketId, conversationId, { atBottom: true });
                    });
                }
            }
        }
    });

    // Server pushes ConversationSynced when a worker syncs conversation data.
    // Invalidate cache and reload if currently viewing this conversation.
    connection.on('ConversationSynced', (ticketId, conversationId) => {
        const key = `${ticketId}:${conversationId}`;
        delete conversationMessages[key];
        if (chatModalTicketId === ticketId && chatModalConversationId === conversationId) {
            reloadConversationPreservingScroll(ticketId, conversationId);
        }
        if (detailChatTicketId === ticketId && detailChatConversationId === conversationId) {
            reloadDetailConversationPreservingScroll(ticketId, conversationId);
        }
    });

    connection.on('ConversationReset', (ticketId, conversationId) => {
        const key = `${ticketId}:${conversationId}`;
        delete conversationMessages[key];
        if (chatModalTicketId === ticketId && chatModalConversationId === conversationId) {
            reloadConversationPreservingScroll(ticketId, conversationId);
        }
        if (detailChatTicketId === ticketId && detailChatConversationId === conversationId) {
            reloadDetailConversationPreservingScroll(ticketId, conversationId);
        }
    });

    connection.on('ConversationFinished', (ticketId, conversationId) => {
        // Mark conversation finished in local state.
        const convos = ticketConversations[ticketId];
        if (convos) {
            const c = convos.find(c => c.id === conversationId);
            if (c) {
                c.isFinished = true;
            }
        }
        if (chatModalTicketId === ticketId && chatModalConversationId === conversationId) {
            setChatInputEnabled(false);
        }
        if (detailChatTicketId === ticketId && detailChatConversationId === conversationId) {
            setDetailChatEnabled(false);
        }
        if (currentDetailTicketId === ticketId) {
            updateConversationDropdown(ticketId);
            updateDetailConversationDropdown(ticketId);
        }
    });

    connection.on('ConversationBusy', (ticketId, conversationId, isBusy) => {
        const key = `${ticketId}:${conversationId}`;
        if (isBusy) {
            busyConversations[key] = true;
        } else {
            delete busyConversations[key];
        }
        updateBusyIndicators(ticketId, conversationId, isBusy);
    });

    connection.onreconnecting(() => {
        updateConnectionStatus('connecting', 'Reconnecting...');
    });

    connection.onreconnected(async () => {
        updateConnectionStatus('connected', 'Connected');
        await loadSettings();
        await refreshAllTickets();

        // Re-subscribe to the ticket group if viewing a ticket detail.
        if (currentDetailTicketId) {
            try {
                await connection.invoke('SubscribeToTicket', currentDetailTicketId);
            } catch (error) {
                console.warn('Could not re-subscribe to ticket updates:', error);
            }
        }
        if (chatModalTicketId && chatModalTicketId !== currentDetailTicketId) {
            try {
                await connection.invoke('SubscribeToTicket', chatModalTicketId);
            } catch (error) {
                console.warn('Could not re-subscribe to chat modal ticket:', error);
            }
        }
    });

    connection.onclose(() => {
        updateConnectionStatus('disconnected', 'Disconnected');
        setTimeout(startConnection, 5000);
    });

    startConnection();
}

async function startConnection() {
    try {
        await connection.start();
        updateConnectionStatus('connected', 'Connected');
        await refreshAllTickets();
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
            settings = normalizeSettings(await response.json());
        }
    } catch (error) {
        console.warn('Could not load settings:', error);
        settings = null;
    }
}

// Flattens the server's { file: { llmConfigs, ... }, systemPrompts } into a flat object
// so the rest of the UI can access settings.llmConfigs directly.
function normalizeSettings(raw) {
    if (!raw) {
        return raw;
    }

    if (raw.file) {
        raw.llmConfigs = raw.file.llmConfigs || [];
        raw.gitConfig = raw.file.gitConfig || {};
        raw.compaction = raw.file.compaction || {};
        raw.webSearch = raw.file.webSearch || {};
    }

    return raw;
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

        const displayMessage = parsed.message.length > 75 ? parsed.message.substring(0, 75) + '…' : parsed.message;

        lastLogHtml = `
            <div class="ticket-activity-log ${logClass}">
                ${parsed.timestamp ? `<span class="activity-timestamp ${logClass}" data-timestamp="${parsed.timestamp}">${formatRelativeTime(parsed.timestamp)}</span>` : ''}
                <span class="activity-message">${escapeHtml(displayMessage)}</span>
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
            <div class="ticket-cost-simple">${ticket.maxCost > 0 ? `$${ticket.llmCost.toFixed(2)} / $${ticket.maxCost.toFixed(2)}` : `$${ticket.llmCost.toFixed(4)}`}</div>
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

    // Preserve chat input text and cursor across re-renders.
    const previousInput = document.getElementById('detailChatInput');
    const savedInputText = previousInput ? previousInput.value : '';
    const savedSelStart = previousInput ? previousInput.selectionStart : 0;
    const savedSelEnd = previousInput ? previousInput.selectionEnd : 0;

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

    // Fetch existing conversations FIRST so buttons can check state properly
    try {
        const convosResponse = await fetch(`${API_BASE}/tickets/${ticketId}/conversations`);
        if (convosResponse.ok) {
            const convos = await convosResponse.json();
            if (convos.length > 0) {
                ticketConversations[ticketId] = convos;
            }
        }
    } catch (error) {
        console.warn('Could not fetch conversations:', error);
    }

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

    // Build latest activity message (shown under title)
    let latestActivityHtml = '';
    if (ticket.activityLog && ticket.activityLog.length > 0) {
        const latestLog = ticket.activityLog[ticket.activityLog.length - 1];
        const parsed = parseLogEntry(latestLog);

        let logClass = '';
        if (parsed.message.includes('Manager:')) {
            logClass = 'manager';
        } else if (parsed.message.includes('Developer:')) {
            logClass = 'developer';
        } else if (parsed.message.includes('Worker:')) {
            logClass = 'worker';
        }

        const displayMessage = parsed.message.length > 75 ? parsed.message.substring(0, 75) + '…' : parsed.message;

        latestActivityHtml = `
            <div class="detail-latest-activity ${logClass}">
                ${parsed.timestamp ? `<span class="activity-timestamp ${logClass}" data-timestamp="${parsed.timestamp}">${formatRelativeTime(parsed.timestamp)}</span>` : ''}
                <span class="activity-message">${escapeHtml(displayMessage)}</span>
            </div>
        `;
    }

    // Build action buttons based on allowed transitions
    let actionsHtml = '';
    const allowedTargets = ALLOWED_TRANSITIONS[status] || [];
    const canDelete = status === 'Backlog' || status === 'Done';
    const canEdit = status === 'Backlog';

    if (status === 'Backlog') {
        actionsHtml += `<button class="btn-primary" onclick="moveTicket('${ticketId}', 'Active')">🚀 Start Work</button>`;
    } else if (status !== 'Done') {
        actionsHtml += `<button class="btn-danger" onclick="moveTicket('${ticketId}', 'Backlog')">↩️ Cancel & Return to Backlog</button>`;
    } else {
        actionsHtml += `<button class="btn-secondary" onclick="moveTicket('${ticketId}', 'Backlog')">↩️ Reopen</button>`;
    }

    // Clear Tasks button - always enabled
    actionsHtml += `<button class="btn-secondary" onclick="clearTasks('${ticketId}')">🧹 Clear Tasks</button>`;

    // Clear Conversation button
    actionsHtml += `<button class="btn-secondary" onclick="clearConversation('${ticketId}')">💬 Clear Conversation</button>`;

    if (canDelete) {
        actionsHtml += `<button class="btn-danger btn-delete" onclick="deleteTicket('${ticketId}')">🗑️ Delete Ticket</button>`;
    }

    actionsHtml += '</div>';

    // Title section
    let titleHtml = `
        <div class="detail-section">
            ${canEdit 
                ? `<input type="text" id="editTitle" class="edit-title-input" value="${escapeHtml(ticket.title)}" placeholder="Ticket title..." onblur="saveTicketDetails('${ticketId}')">`
                : `<h2 class="detail-title">${escapeHtml(ticket.title)}</h2>`
            }
        </div>
    `;

    // Description accordion
    let descriptionAccordionHtml = `
        <div class="accordion collapsed" id="descriptionAccordion">
            <div class="accordion-header">
                <span>📝 Description</span>
                <span class="accordion-icon">▼</span>
            </div>
            <div class="accordion-content">
                ${canEdit 
                    ? `<textarea id="editDescription" class="edit-description-input" rows="4" placeholder="Description..." onblur="saveTicketDetails('${ticketId}')">${escapeHtml(ticket.description || '')}</textarea>`
                    : `${escapeHtml(ticket.description || 'No description provided.')}`
                }
            </div>
        </div>
    `;

    detailDiv.innerHTML = `
        <div class="ticket-detail-header">
            <span class="status-badge ${status.toLowerCase()}">${status}</span>
            ${ticket.branchName ? `<span class="branch-name">🌿 ${escapeHtml(ticket.branchName)}</span>` : '<span class="branch-name-spacer"></span>'}
            <div class="ticket-detail-right">
                <span class="ticket-detail-id">#${ticket.id}</span>
                <span class="ticket-detail-cost">
                    $${ticket.llmCost.toFixed(2)} / 
                    ${status === 'Done' 
                        ? `$${ticket.maxCost.toFixed(2)}` 
                        : `$<input type="number" id="maxCostInput" class="cost-inline-input" value="${ticket.maxCost.toFixed(2)}" min="0" step="0.01" onchange="updateMaxCost('${ticket.id}')">`
                    }
                </span>
            </div>
        </div>

        ${latestActivityHtml}

        ${titleHtml}

        ${descriptionAccordionHtml}

        <div class="accordion" id="tasksAccordion">
            <div class="accordion-header">
                <span>📋 Tasks</span>
                <span class="accordion-icon">▼</span>
            </div>
            <div class="accordion-content">
                ${tasksHtml}
            </div>
        </div>

        <div class="accordion collapsed" id="chatAccordion">
            <div class="accordion-header">
                <span>💬 Chat</span>
                <span class="accordion-icon">▼</span>
            </div>
            <div class="accordion-content">
                <div class="detail-chat-header">
                    ${buildPlannerLlmDropdown(ticket, 'detailPlannerLlm')}
                    <select id="detailConversationDropdown" class="detail-chat-select">
                        <option value="">💬 Select conversation…</option>
                    </select>
                    <button id="detailDeleteConvoBtn" class="detail-chat-maximize" title="Delete finished conversation" style="display:none;" onclick="deleteSelectedDetailConversation()">🗑️</button>
                    <button class="detail-chat-maximize" onclick="openChat('${ticketId}')" title="Maximize chat">⤢</button>
                </div>
                <div class="detail-chat-messages" id="detailChatMessages">
                    <div class="chat-msg chat-msg-system">Select a conversation above.</div>
                </div>
                <div class="detail-chat-input-area">
                    <textarea id="detailChatInput" class="detail-chat-input" placeholder="Type a message…" rows="1" disabled></textarea>
                    <button id="detailChatStopBtn" class="btn-danger detail-chat-stop" title="Stop" onclick="interruptConversation()">■</button>
                    <button id="detailChatSendBtn" class="btn-primary detail-chat-send" disabled>→</button>
                </div>
            </div>
        </div>
    `;

    const actionsDiv = document.getElementById('ticketDetailActions');
    actionsDiv.innerHTML = `<div class="ticket-actions-container">${actionsHtml}</div>`;

    modal.classList.add('active');

    // Restore accordion states from localStorage
    const accordionStates = getTicketAccordionStates(ticketId);

    const descAccordion = detailDiv.querySelector('#descriptionAccordion');
    const tasksAccordion = detailDiv.querySelector('#tasksAccordion');
    const chatAccordion = detailDiv.querySelector('#chatAccordion');

    if (descAccordion) {
        if (accordionStates.description) {
            descAccordion.classList.remove('collapsed');
        } else {
            descAccordion.classList.add('collapsed');
        }
    }

    if (tasksAccordion) {
        if (accordionStates.tasks) {
            tasksAccordion.classList.remove('collapsed');
        } else {
            tasksAccordion.classList.add('collapsed');
        }
    }

    if (chatAccordion) {
        if (accordionStates.chat) {
            chatAccordion.classList.remove('collapsed');
        } else {
            chatAccordion.classList.add('collapsed');
        }
    }

    // Setup accordion click handlers with state saving
    const descAccordionHeader = detailDiv.querySelector('#descriptionAccordion .accordion-header');
    const tasksAccordionHeader = detailDiv.querySelector('#tasksAccordion .accordion-header');
    const chatAccordionHeader = detailDiv.querySelector('#chatAccordion .accordion-header');

    if (descAccordionHeader) {
        descAccordionHeader.addEventListener('click', function() {
            const accordion = this.closest('.accordion');
            accordion.classList.toggle('collapsed');
            const states = getTicketAccordionStates(ticketId);
            states.description = !accordion.classList.contains('collapsed');
            saveTicketAccordionStates(ticketId, states);
        });
    }

    if (tasksAccordionHeader) {
        tasksAccordionHeader.addEventListener('click', function() {
            const accordion = this.closest('.accordion');
            accordion.classList.toggle('collapsed');
            const states = getTicketAccordionStates(ticketId);
            states.tasks = !accordion.classList.contains('collapsed');
            saveTicketAccordionStates(ticketId, states);
        });
    }

    if (chatAccordionHeader) {
        chatAccordionHeader.addEventListener('click', function() {
            const accordion = this.closest('.accordion');
            accordion.classList.toggle('collapsed');
            const states = getTicketAccordionStates(ticketId);
            states.chat = !accordion.classList.contains('collapsed');
            saveTicketAccordionStates(ticketId, states);
        });
    }

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

    // Setup inline chat
    setupDetailChat(ticketId);

    // Restore chat input text and cursor.
    if (savedInputText) {
        const restoredInput = document.getElementById('detailChatInput');
        if (restoredInput) {
            restoredInput.value = savedInputText;
            restoredInput.selectionStart = savedSelStart;
            restoredInput.selectionEnd = savedSelEnd;
        }
    }

    // Setup resize handle
    setupDetailResize();
}

// Horizontal splitter drag between two panes.
function setupSplitter(splitterId, topPaneId, bottomPaneId) {
    const splitter = document.getElementById(splitterId);
    const topPane = document.getElementById(topPaneId);
    const bottomPane = document.getElementById(bottomPaneId);
    if (!splitter || !topPane || !bottomPane) return;

    let startY = 0;
    let startTopH = 0;
    let startBottomH = 0;

    function onMouseDown(e) {
        e.preventDefault();
        startY = e.clientY;
        startTopH = topPane.offsetHeight;
        startBottomH = bottomPane.offsetHeight;
        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup', onMouseUp);
        document.body.style.cursor = 'row-resize';
        document.body.style.userSelect = 'none';
    }

    function onMouseMove(e) {
        const delta = e.clientY - startY;
        const newTop = Math.max(40, startTopH + delta);
        const newBottom = Math.max(40, startBottomH - delta);
        topPane.style.height = newTop + 'px';
        topPane.style.flex = 'none';
        bottomPane.style.height = newBottom + 'px';
        bottomPane.style.flex = 'none';
    }

    function onMouseUp() {
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup', onMouseUp);
        document.body.style.cursor = '';
        document.body.style.userSelect = '';
    }

    splitter.addEventListener('mousedown', onMouseDown);
}

// Inline chat inside the ticket detail modal.
let detailChatTicketId = null;
let detailChatConversationId = null;

function setupDetailChat(ticketId) {
    const previousConversationId = (detailChatTicketId === ticketId) ? detailChatConversationId : null;
    detailChatTicketId = ticketId;
    detailChatConversationId = null;

    const dropdown = document.getElementById('detailConversationDropdown');
    const messagesDiv = document.getElementById('detailChatMessages');
    const input = document.getElementById('detailChatInput');
    const sendBtn = document.getElementById('detailChatSendBtn');
    const deleteBtn = document.getElementById('detailDeleteConvoBtn');
    if (!dropdown || !messagesDiv || !input || !sendBtn) return;

    // Populate dropdown.
    const convos = sortConversations(ticketConversations[ticketId] || []);
    dropdown.innerHTML = '<option value="">💬 Select conversation…</option>';
    for (let i = 0; i < convos.length; i++) {
        const c = convos[i];
        const suffix = c.isFinished ? ' ✓' : ` (${c.messageCount})`;
        const option = document.createElement('option');
        option.value = c.id;
        option.textContent = c.displayName + suffix;
        dropdown.appendChild(option);
    }

    // Determine which conversation to select: in-memory state > localStorage > most active.
    let selectedConvId = previousConversationId;
    let scrollState = { atBottom: true };

    if (!selectedConvId || !convos.some(c => c.id === selectedConvId)) {
        const saved = getTicketConversationState(ticketId);
        if (saved && convos.some(c => c.id === saved.conversationId)) {
            selectedConvId = saved.conversationId;
            scrollState = saved.scrollState || { atBottom: true };
        } else {
            selectedConvId = pickDefaultConversation(convos);
            scrollState = { atBottom: true };
        }
    }

    // Restore selection if valid.
    if (selectedConvId && convos.some(c => c.id === selectedConvId)) {
        dropdown.value = selectedConvId;
        detailChatConversationId = selectedConvId;
        const info = convos.find(c => c.id === selectedConvId);
        setDetailChatEnabled(!(info && info.isFinished));
        if (deleteBtn) deleteBtn.style.display = (info && info.isFinished) ? '' : 'none';
        loadAndRestoreDetailConversation(ticketId, selectedConvId, scrollState);
        updateBusyIndicators(ticketId, selectedConvId, !!busyConversations[`${ticketId}:${selectedConvId}`]);
    }

    dropdown.addEventListener('change', () => {
        const convId = dropdown.value;
        if (convId) {
            detailChatConversationId = convId;
            loadAndDisplayDetailConversation(ticketId, convId);
            const info = convos.find(c => c.id === convId);
            setDetailChatEnabled(!(info && info.isFinished));
            if (deleteBtn) deleteBtn.style.display = (info && info.isFinished) ? '' : 'none';
            saveTicketConversationState(ticketId, convId, { atBottom: true });
            updateBusyIndicators(ticketId, convId, !!busyConversations[`${ticketId}:${convId}`]);
        } else {
            detailChatConversationId = null;
            messagesDiv.innerHTML = '<div class="chat-msg chat-msg-system">Select a conversation above.</div>';
            setDetailChatEnabled(false);
            if (deleteBtn) deleteBtn.style.display = 'none';
            updateBusyIndicators(ticketId, '', false);
        }
    });

    function sendMessage() {
        const text = input.value.trim();
        if (!text || !detailChatTicketId || !detailChatConversationId) return;
        if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;

        input.value = '';
        input.style.height = '';
        connection.invoke('SendChatToWorker', detailChatTicketId, detailChatConversationId, text).catch(() => {});
    }

    sendBtn.addEventListener('click', sendMessage);
    input.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendMessage();
        }
    });
    input.addEventListener('input', () => {
        input.style.height = '';
        input.style.height = Math.min(input.scrollHeight, 80) + 'px';
    });

    // Track scroll position with debouncing.
    let scrollTimeout = null;
    messagesDiv.addEventListener('scroll', () => {
        if (!detailChatTicketId || !detailChatConversationId) return;
        clearTimeout(scrollTimeout);
        scrollTimeout = setTimeout(() => {
            const atBottom = messagesDiv.scrollHeight - messagesDiv.scrollTop - messagesDiv.clientHeight < 40;
            const scrollState = atBottom ? { atBottom: true } : { messageIndex: messagesDiv.children.length - 1 };
            saveTicketConversationState(detailChatTicketId, detailChatConversationId, scrollState);
        }, 300);
    });
}

function setDetailChatEnabled(enabled) {
    const input = document.getElementById('detailChatInput');
    const btn = document.getElementById('detailChatSendBtn');
    if (input) {
        input.disabled = !enabled;
        input.placeholder = enabled ? 'Type a message…' : 'Conversation finished.';
    }
    if (btn) btn.disabled = !enabled;
}

async function loadAndDisplayDetailConversation(ticketId, conversationId) {
    const messagesDiv = document.getElementById('detailChatMessages');
    if (!messagesDiv) return;

    const key = `${ticketId}:${conversationId}`;
    let msgs = conversationMessages[key];

    if (!msgs) {
        try {
            const response = await fetch(`${API_BASE}/tickets/${ticketId}/conversations/${conversationId}`);
            if (response.ok) {
                const data = await response.json();
                msgs = data.messages || [];
                conversationMessages[key] = msgs;
            } else {
                msgs = [];
            }
        } catch {
            msgs = [];
        }
    }

    messagesDiv.innerHTML = '';
    for (let i = 0; i < msgs.length; i++) {
        renderDetailChatMessage(msgs, i, messagesDiv);
    }

    requestAnimationFrame(() => {
        messagesDiv.scrollTop = messagesDiv.scrollHeight;
    });
}

// Loads and displays a detail conversation, then restores scroll state.
async function loadAndRestoreDetailConversation(ticketId, conversationId, scrollState) {
    const messagesDiv = document.getElementById('detailChatMessages');
    if (!messagesDiv) return;

    const key = `${ticketId}:${conversationId}`;
    let msgs = conversationMessages[key];

    if (!msgs) {
        try {
            const response = await fetch(`${API_BASE}/tickets/${ticketId}/conversations/${conversationId}`);
            if (response.ok) {
                const data = await response.json();
                msgs = data.messages || [];
                conversationMessages[key] = msgs;
            } else {
                msgs = [];
            }
        } catch {
            msgs = [];
        }
    }

    messagesDiv.innerHTML = '';
    for (let i = 0; i < msgs.length; i++) {
        renderDetailChatMessage(msgs, i, messagesDiv);
    }

    requestAnimationFrame(() => {
        if (scrollState.atBottom) {
            messagesDiv.scrollTop = messagesDiv.scrollHeight;
        } else if (scrollState.messageIndex != null) {
            const targetChild = messagesDiv.children[Math.min(scrollState.messageIndex, messagesDiv.children.length - 1)];
            if (targetChild) {
                targetChild.scrollIntoView({ block: 'start' });
            }
        } else {
            messagesDiv.scrollTop = messagesDiv.scrollHeight;
        }
    });
}

function renderDetailChatMessage(messages, index, container) {
    const msg = messages[index];

    if (msg.role === 'user') {
        const content = (msg.content || '');
        appendDetailChatBubble('user', content);
    } else if (msg.role === 'assistant') {
        if (msg.content) {
            appendDetailChatBubble('assistant', msg.content);
        }
        if (msg.tool_calls) {
            for (let i = 0; i < msg.tool_calls.length; i++) {
                const tc = msg.tool_calls[i];
                // Look ahead for the matching tool result
                let result = null;
                for (let j = index + 1; j < messages.length; j++) {
                    if (messages[j].role === 'tool' && messages[j].tool_call_id === tc.id) {
                        result = messages[j].content || '';
                        break;
                    }
                }
                appendDetailToolCall(container, tc.function.name, tc.function.arguments, result);
            }
        }
    } else if (msg.role === 'system') {
        appendDetailChatBubble('system', msg.content || '');
    }
    // Skip role === 'tool' — results are embedded in tool call accordions
}

function appendDetailToolCall(container, name, argsJson, result) {
    const args = formatToolCallParams(argsJson);
    const el = document.createElement('div');
    el.className = 'chat-tool-accordion';

    const resultHtml = result ? `
        <div class="chat-tool-section chat-tool-result">
            <pre>${escapeHtml(result)}</pre>
        </div>
    ` : '';

    el.innerHTML = `
        <div class="chat-tool-header">
            <span class="chat-tool-icon">▶</span>
            <span class="chat-tool-name">${escapeHtml(name)}</span>
            <span class="chat-tool-params">${escapeHtml(args)}</span>
        </div>
        <div class="chat-tool-body">
            ${resultHtml}
        </div>
    `;
    el.querySelector('.chat-tool-header').addEventListener('click', () => {
        const icon = el.querySelector('.chat-tool-icon');
        const body = el.querySelector('.chat-tool-body');
        if (body.classList.contains('open')) {
            body.classList.remove('open');
            icon.classList.remove('open');
        } else {
            body.classList.add('open');
            icon.classList.add('open');
        }
    });
    container.appendChild(el);
}

function formatToolCallParams(argsJson) {
    try {
        const obj = JSON.parse(argsJson);
        const parts = [];
        for (const key in obj) {
            let val = String(obj[key]);
            if (val.length > 40) {
                val = val.substring(0, 40) + '…';
            }
            parts.push(val);
        }
        return parts.join(' ');
    } catch {
        return argsJson.length > 80 ? argsJson.substring(0, 80) + '…' : argsJson;
    }
}

function appendDetailChatBubble(role, content) {
    const messagesDiv = document.getElementById('detailChatMessages');
    if (!messagesDiv) return;
    const el = document.createElement('div');
    el.className = `chat-msg chat-msg-${role}`;
    el.textContent = content;
    messagesDiv.appendChild(el);
    messagesDiv.scrollTop = messagesDiv.scrollHeight;
}

// Resize the detail modal from the bottom-right corner.
function setupDetailResize() {
    const handle = document.getElementById('detailResizeHandle');
    const modalContent = handle ? handle.closest('.detail-modal-content') : null;
    const modal = handle ? handle.closest('.modal') : null;
    if (!handle || !modalContent || !modal) return;

    let startX = 0;
    let startY = 0;
    let startW = 0;
    let startH = 0;
    let startLeft = 0;
    let startTop = 0;

    function onMouseDown(e) {
        e.preventDefault();
        e.stopPropagation();

        // Capture initial state.
        const rect = modalContent.getBoundingClientRect();
        startX = e.clientX;
        startY = e.clientY;
        startW = rect.width;
        startH = rect.height;
        startLeft = rect.left;
        startTop = rect.top;

        // Switch to absolute positioning so we can control position during resize.
        modal.style.alignItems = 'flex-start';
        modalContent.style.position = 'absolute';
        modalContent.style.left = startLeft + 'px';
        modalContent.style.top = startTop + 'px';
        modalContent.style.margin = '0';

        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup', onMouseUp);
        document.body.style.cursor = 'nwse-resize';
        document.body.style.userSelect = 'none';
    }

    function onMouseMove(e) {
        const deltaX = e.clientX - startX;
        const deltaY = e.clientY - startY;

        const newW = Math.max(400, startW + deltaX);
        const newH = Math.max(300, startH + deltaY);

        // Keep top-left corner fixed, bottom-right follows mouse.
        modalContent.style.width = newW + 'px';
        modalContent.style.maxWidth = newW + 'px';
        modalContent.style.height = newH + 'px';
        modalContent.style.maxHeight = newH + 'px';
    }

    function onMouseUp() {
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup', onMouseUp);
        document.body.style.cursor = '';
        document.body.style.userSelect = '';

        // Leave the modal in absolute positioning so it stays where the user placed it.
    }

    handle.addEventListener('mousedown', onMouseDown);
}

// Move ticket to new status
async function moveTicket(ticketId, newStatus) {
    // Close modal immediately before API call to prevent SignalR race condition
    if (currentDetailTicketId === ticketId) {
        document.getElementById('ticketDetailModal').classList.remove('active');
        currentDetailTicketId = null;
        detailChatTicketId = null;
        detailChatConversationId = null;
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

// Update max cost for a ticket
async function updateMaxCost(ticketId) {
    const input = document.getElementById('maxCostInput');
    const maxCost = parseFloat(input.value) || 0;

    try {
        const response = await fetch(`${API_BASE}/tickets/${ticketId}/maxcost`, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ maxCost })
        });

        if (response.ok) {
            await refreshAllTickets();
            await showTicketDetails(ticketId);
        } else {
            console.error('Failed to update max cost');
        }
    } catch (error) {
        console.error('Error updating max cost:', error);
    }
}

window.updateMaxCost = updateMaxCost;

// Build the planner LLM dropdown HTML
function buildPlannerLlmDropdown(ticket, elementId) {
    const llmConfigs = (settings && settings.llmConfigs) || [];
    if (llmConfigs.length === 0) {
        return '';
    }

    const currentId = ticket.plannerLlmId || '';
    let options = `<option value=""${currentId === '' ? ' selected' : ''}>🤖 Auto</option>`;

    llmConfigs.forEach(config => {
        const id = config.id || '';
        const model = config.model || 'Unknown';
        const selected = id === currentId ? ' selected' : '';
        options += `<option value="${escapeHtml(id)}"${selected}>${escapeHtml(model)}</option>`;
    });

    return `<select id="${elementId}" class="detail-chat-select" style="max-width: 160px;" onchange="updatePlannerLlm('${ticket.id}', this.value)" title="Planner LLM">${options}</select>`;
}

// Update the planner LLM for a ticket and sync both dropdowns
async function updatePlannerLlm(ticketId, llmId) {
    // Sync both dropdowns so they stay in sync.
    const detailSelect = document.getElementById('detailPlannerLlm');
    const chatSelect = document.getElementById('chatPlannerLlm');
    if (detailSelect) {
        detailSelect.value = llmId;
    }
    if (chatSelect) {
        chatSelect.value = llmId;
    }

    try {
        const response = await fetch(`${API_BASE}/tickets/${ticketId}/plannerllm`, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ plannerLlmId: llmId || null })
        });

        if (!response.ok) {
            console.error('Failed to update planner LLM');
        }
    } catch (error) {
        console.error('Error updating planner LLM:', error);
    }
}

window.updatePlannerLlm = updatePlannerLlm;

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
            // Clean up conversation data for deleted ticket.
            delete ticketConversations[ticketId];
            // Clean cached conversation messages.
            Object.keys(conversationMessages).forEach(key => {
                if (key.startsWith(`${ticketId}:`)) {
                    delete conversationMessages[key];
                }
            });

            if (chatModalTicketId === ticketId) {
                closeChatModal();
            }

            await refreshAllTickets();

            // Close detail modal
            document.getElementById('ticketDetailModal').classList.remove('active');
            currentDetailTicketId = null;
            detailChatTicketId = null;
            detailChatConversationId = null;
        } else {
            console.error('Failed to delete ticket');
        }
    } catch (error) {
        console.error('Error deleting ticket:', error);
    }
}

// Make deleteTicket available globally for onclick handlers
window.deleteTicket = deleteTicket;

// Save ticket title and description
async function saveTicketDetails(ticketId) {
    const titleEl = document.getElementById('editTitle');
    const descriptionEl = document.getElementById('editDescription');

    if (!titleEl || !descriptionEl) {
        return;
    }

    const title = titleEl.value.trim();
    const description = descriptionEl.value.trim();

    if (!title) {
        return;
    }

    try {
        const response = await fetch(`${API_BASE}/tickets/${ticketId}/details`, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ title, description })
        });

        if (response.ok) {
            await refreshAllTickets();
            await showTicketDetails(ticketId);
        } else {
            console.error('Failed to save ticket details');
        }
    } catch (error) {
        console.error('Error saving ticket details:', error);
    }
}

window.saveTicketDetails = saveTicketDetails;

// Clear all tasks from a ticket
async function clearTasks(ticketId) {
    if (!ticketId) {
        console.warn('clearTasks called with invalid ticketId:', ticketId);
        return;
    }

    if (!confirm('Clear all tasks and subtasks from this ticket?')) {
        return;
    }

    try {
        const response = await fetch(`${API_BASE}/tickets/${ticketId}/tasks`, {
            method: 'DELETE'
        });

        if (response.ok) {
            await refreshAllTickets();
            await showTicketDetails(ticketId);
        } else {
            console.error('Failed to clear tasks');
        }
    } catch (error) {
        console.error('Error clearing tasks:', error);
    }
}

window.clearTasks = clearTasks;

// Clear a conversation back to its initial state
async function clearConversation(ticketId) {
    console.log('clearConversation called with ticketId:', ticketId);
    if (!ticketId) {
        console.warn('clearConversation: no ticketId');
        return;
    }

    const convos = ticketConversations[ticketId] || [];
    console.log('clearConversation: found', convos.length, 'conversations, finished states:', convos.map(c => c.isFinished));
    const activeConvo = convos.find(c => !c.isFinished);
    if (!activeConvo) {
        alert('No active conversation to clear.');
        return;
    }

    if (!confirm('Reset this conversation? All messages and memories will be cleared.')) {
        return;
    }

    console.log('clearConversation: invoking RequestClearConversation', ticketId, activeConvo.id);
    try {
        await connection.invoke('RequestClearConversation', ticketId, activeConvo.id);
        console.log('clearConversation: invoke completed successfully');
    } catch (error) {
        console.error('Error clearing conversation:', error);
    }
}

window.clearConversation = clearConversation;

async function deleteConversation(ticketId, conversationId) {
    if (!ticketId || !conversationId) return;
    const convos = ticketConversations[ticketId] || [];
    const info = convos.find(c => c.id === conversationId);
    if (!info || !info.isFinished) return;

    try {
        const resp = await fetch(`${API_BASE}/tickets/${ticketId}/conversations/${conversationId}`, { method: 'DELETE' });
        if (!resp.ok) {
            console.warn('Failed to delete conversation:', resp.status);
        }
    } catch (err) {
        console.error('Error deleting conversation:', err);
    }
}

window.deleteConversation = deleteConversation;

function deleteSelectedDetailConversation() {
    if (detailChatTicketId && detailChatConversationId) {
        deleteConversation(detailChatTicketId, detailChatConversationId);
    }
}

window.deleteSelectedDetailConversation = deleteSelectedDetailConversation;

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

    // Compaction type dropdown
    document.getElementById('compactionType').addEventListener('change', updateCompactionVisibility);

    // Close modal buttons
    document.querySelectorAll('.close').forEach(btn => {
        btn.addEventListener('click', () => {
            const modal = btn.closest('.modal');

            if (modal.id === 'settingsModal') {
                saveSettings();
            }

            modal.classList.remove('active');

            if (modal.id === 'ticketDetailModal') {
                currentDetailTicketId = null;
                detailChatTicketId = null;
                detailChatConversationId = null;
            }
        });
    });

    // Close modals on backdrop click
    document.querySelectorAll('.modal').forEach(modal => {
        modal.addEventListener('click', (e) => {
            if (e.target === modal) {
                if (modal.id === 'settingsModal') {
                    saveSettings();
                }

                modal.classList.remove('active');

                if (modal.id === 'ticketDetailModal') {
                    currentDetailTicketId = null;
                    detailChatTicketId = null;
                    detailChatConversationId = null;
                }
            }
        });
    });

    // Close modals on Escape key
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            document.querySelectorAll('.modal.active').forEach(modal => {
                if (modal.id === 'settingsModal') {
                    saveSettings();
                }

                modal.classList.remove('active');

                if (modal.id === 'ticketDetailModal') {
                    currentDetailTicketId = null;
                    detailChatTicketId = null;
                    detailChatConversationId = null;
                }

                if (modal.id === 'chatModal') {
                    closeChatModal();
                }
            });
        }
    });
}

async function handleCreateTicket(e) {
    e.preventDefault();

    const title = document.getElementById('ticketTitle').value.trim();
    const description = document.getElementById('ticketDescription').value.trim();
    const maxCost = parseFloat(document.getElementById('ticketMaxCost').value) || 0;

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
                status: 'Backlog',
                maxCost
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
    const compactionType = (settings && settings.compaction && settings.compaction.type) || 'disabled';
    document.getElementById('compactionType').value = compactionType === 'summarize' ? 'summarize' : 'disabled';

    const contextPercent = Math.round(((settings && settings.compaction && settings.compaction.contextSizePercent) || 0.9) * 100);
    document.getElementById('contextPercent').value = contextPercent;
    updatePercentLabel();
    updateCompactionVisibility();

    // Populate LLM configs
    renderLLMConfigs();

    // Setup accordion handlers
    setupAccordions();

    // Restore settings accordion states from localStorage.
    const accordionStates = getSettingsAccordionStates();
    document.querySelectorAll('#settingsContent > .accordion').forEach(accordion => {
        const header = accordion.querySelector('.accordion-header');
        const key = header ? header.getAttribute('data-accordion') : null;
        if (key && accordionStates[key]) {
            accordion.classList.remove('collapsed');
        } else {
            accordion.classList.add('collapsed');
        }
    });

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

    // Persist settings accordion states.
    const header = accordion.querySelector('.accordion-header');
    const key = header ? header.getAttribute('data-accordion') : null;
    if (key && accordion.closest('#settingsContent')) {
        saveSettingsAccordionState(key, !accordion.classList.contains('collapsed'));
    }
}

function updateCompactionVisibility() {
    const compactionType = document.getElementById('compactionType').value;
    document.getElementById('compactionOptions').style.display = compactionType === 'summarize' ? 'block' : 'none';
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
        configEl.className = 'llm-config accordion collapsed';
        configEl.style.marginBottom = '8px';
        configEl.style.padding = '0';
        const modelName = config.model || `LLM ${index + 1}`;
        const accordionKey = `llm-${config.model || index}`;
        configEl.innerHTML = `
            <div class="accordion-header" data-accordion="${escapeHtml(accordionKey)}" style="padding: 10px 12px; margin: 0;">
                <span>🧠 ${escapeHtml(modelName)}</span>
                <span class="accordion-icon">▼</span>
            </div>
            <div class="accordion-content" style="padding: 12px; padding-top: 8px;">
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
                <div style="display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; margin-bottom: 15px;">
                    <div>
                        <label style="font-size: 11px; font-weight: 500; color: var(--text-muted); display: block; margin-bottom: 4px;">Context Length</label>
                        <input type="number" class="llm-context-length" value="${config.contextLength || 128000}" min="1000" step="1000" placeholder="128000" style="width: 100%; padding: 0.5rem 0.5rem; border: 1px solid var(--gray-300); border-radius: var(--radius); background: var(--gray-100); color: var(--gray-800); font-size: 0.9375rem; transition: border-color 0.2s ease, box-shadow 0.2s ease; font-family: inherit;">
                    </div>
                    <div>
                        <label style="font-size: 11px; font-weight: 500; color: var(--text-muted); display: block; margin-bottom: 4px;">Input $/M</label>
                        <input type="number" class="llm-input-price" value="${config.inputTokenPrice || 0}" min="0" step="0.01" placeholder="0.00" style="width: 100%; padding: 0.5rem 0.5rem; border: 1px solid var(--gray-300); border-radius: var(--radius); background: var(--gray-100); color: var(--gray-800); font-size: 0.9375rem; transition: border-color 0.2s ease, box-shadow 0.2s ease; font-family: inherit;">
                    </div>
                    <div>
                        <label style="font-size: 11px; font-weight: 500; color: var(--text-muted); display: block; margin-bottom: 4px;">Output $/M</label>
                        <input type="number" class="llm-output-price" value="${config.outputTokenPrice || 0}" min="0" step="0.01" placeholder="0.00" style="width: 100%; padding: 0.5rem 0.5rem; border: 1px solid var(--gray-300); border-radius: var(--radius); background: var(--gray-100); color: var(--gray-800); font-size: 0.9375rem; transition: border-color 0.2s ease, box-shadow 0.2s ease; font-family: inherit;">
                    </div>
                    <div>
                        <label style="font-size: 11px; font-weight: 500; color: var(--text-muted); display: block; margin-bottom: 4px;">Temperature</label>
                        <input type="number" class="llm-temperature" value="${config.temperature !== undefined ? config.temperature : 0.2}" min="0" max="2" step="0.1" placeholder="0.2" style="width: 100%; padding: 0.5rem 0.5rem; border: 1px solid var(--gray-300); border-radius: var(--radius); background: var(--gray-100); color: var(--gray-800); font-size: 0.9375rem; transition: border-color 0.2s ease, box-shadow 0.2s ease; font-family: inherit;">
                    </div>
                </div>
                <div class="form-group">
                    <label>Strengths</label>
                    <input type="text" class="llm-strengths" value="${escapeHtml(config.strengths || '')}" placeholder="e.g. Strong at coding, large context window">
                </div>
                <div class="form-group">
                    <label>Weaknesses</label>
                    <input type="text" class="llm-weaknesses" value="${escapeHtml(config.weaknesses || '')}" placeholder="e.g. Slow, expensive, poor at UI work">
                </div>
                <button type="button" class="btn-danger btn-sm" data-index="${index}" style="width: 100%; display: flex; align-items: center; justify-content: center;">Remove This LLM</button>
            </div>
        `;

        configEl.querySelector('.btn-danger').addEventListener('click', () => removeLLMConfig(index));
        configEl.querySelector('.accordion-header').addEventListener('click', toggleAccordion);

        // Restore expanded state from localStorage.
        const savedStates = getSettingsAccordionStates();
        if (savedStates[accordionKey]) {
            configEl.classList.remove('collapsed');
        }

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

    settings.llmConfigs.push({ model: '', apiKey: '', endpoint: '', contextLength: 128000, inputTokenPrice: 0, outputTokenPrice: 0, temperature: 0.2, strengths: '', weaknesses: '' });
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
            contextLength: parseInt(configEl.querySelector('.llm-context-length').value, 10) || 128000,
            inputTokenPrice: parseFloat(configEl.querySelector('.llm-input-price').value) || 0,
            outputTokenPrice: parseFloat(configEl.querySelector('.llm-output-price').value) || 0,
            temperature: parseFloat(configEl.querySelector('.llm-temperature').value) || 0.2,
            strengths: configEl.querySelector('.llm-strengths').value || '',
            weaknesses: configEl.querySelector('.llm-weaknesses').value || ''
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
        file: {
            llmConfigs: collectLLMConfigs(),
            gitConfig: {
                repositoryUrl: document.getElementById('gitUrl').value,
                username: document.getElementById('gitUsername').value,
                email: document.getElementById('gitEmail').value,
                sshKey: document.getElementById('gitSshKey').value || null,
                apiToken: document.getElementById('gitApiToken').value || null,
                password: document.getElementById('gitPassword').value || null
            },
            compaction: {
                type: document.getElementById('compactionType').value,
                contextSizePercent: parseInt(document.getElementById('contextPercent').value, 10) / 100
            }
        }
    };

    try {
        const response = await fetch(`${API_BASE}/settings`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(updatedSettings)
        });

        if (response.ok) {
            settings = normalizeSettings(await response.json());
        } else {
            console.error('Failed to save settings');
        }
    } catch (error) {
        console.error('Error saving settings:', error);
    }
}

// Update percent label when slider moves
function updatePercentLabel() {
    const slider = document.getElementById('contextPercent');
    const label = document.getElementById('contextPercentValue');
    label.textContent = `${slider.value}%`;
}

window.updatePercentLabel = updatePercentLabel;

// Make saveSettings available globally for onclick
window.saveSettings = saveSettings;

// ---- Chat functionality ----

// Conversation state.
let ticketConversations = {};    // ticketId -> [{id, displayName, messageCount, isFinished}]
let conversationMessages = {};   // "ticketId:conversationId" -> [ConversationMessage]
let ticketPendingToolCalls = {}; // ticketId -> {toolCallId -> domElement}
let chatModalTicketId = null;
let chatModalConversationId = null;

// Tracks which conversations are currently busy (LLM running).
// Key: "ticketId:conversationId", Value: true
let busyConversations = {};

// LocalStorage helpers for persistent conversation state.
function getTicketConversationState(ticketId) {
    try {
        const stored = localStorage.getItem(`ticket-${ticketId}-chat`);
        return stored ? JSON.parse(stored) : null;
    } catch {
        return null;
    }
}

function saveTicketConversationState(ticketId, conversationId, scrollState) {
    try {
        const state = { conversationId, scrollState };
        localStorage.setItem(`ticket-${ticketId}-chat`, JSON.stringify(state));
    } catch (e) {
        console.warn('Failed to save conversation state:', e);
    }
}

// LocalStorage helpers for accordion states
function getTicketAccordionStates(ticketId) {
    try {
        const stored = localStorage.getItem(`ticket-${ticketId}-accordions`);
        return stored ? JSON.parse(stored) : { description: false, tasks: true, chat: false };
    } catch {
        return { description: false, tasks: true, chat: false };
    }
}

function saveTicketAccordionStates(ticketId, states) {
    try {
        localStorage.setItem(`ticket-${ticketId}-accordions`, JSON.stringify(states));
    } catch (e) {
        console.warn('Failed to save accordion states:', e);
    }
}

function getSettingsAccordionStates() {
    try {
        const stored = localStorage.getItem('settings-accordions');
        return stored ? JSON.parse(stored) : {};
    } catch {
        return {};
    }
}

function saveSettingsAccordionState(key, expanded) {
    try {
        const states = getSettingsAccordionStates();
        states[key] = expanded;
        localStorage.setItem('settings-accordions', JSON.stringify(states));
    } catch (e) {
        console.warn('Failed to save settings accordion state:', e);
    }
}

// Sorts conversations: unfinished first, then finished. Within each group, newest first by startedAt.
function sortConversations(convos) {
    return [...convos].sort((a, b) => {
        if (a.isFinished !== b.isFinished) return a.isFinished ? 1 : -1;
        const ta = a.startedAt || '';
        const tb = b.startedAt || '';
        if (ta > tb) return -1;
        if (ta < tb) return 1;
        return 0;
    });
}

function pickDefaultConversation(convos) {
    if (!convos || convos.length === 0) return null;
    // Prefer active (not finished) conversations with higher message count.
    const active = convos.filter(c => !c.isFinished).sort((a, b) => b.messageCount - a.messageCount);
    if (active.length > 0) return active[0].id;
    // Fallback to any conversation sorted by message count.
    const sorted = [...convos].sort((a, b) => b.messageCount - a.messageCount);
    return sorted[0].id;
}

// Updates the conversation dropdown in the ticket detail view.
function updateConversationDropdown(ticketId) {
    const dropdown = document.getElementById('conversationDropdown');
    if (!dropdown) {
        return;
    }

    const convos = sortConversations(ticketConversations[ticketId] || []);
    const previousValue = dropdown.value;

    dropdown.innerHTML = '<option value="">💬 Select conversation…</option>';
    for (let i = 0; i < convos.length; i++) {
        const c = convos[i];
        const suffix = c.isFinished ? ' ✓' : ` (${c.messageCount})`;
        const option = document.createElement('option');
        option.value = c.id;
        option.textContent = c.displayName + suffix;
        dropdown.appendChild(option);
    }

    // Preserve selection if still valid.
    if (previousValue && convos.some(c => c.id === previousValue)) {
        dropdown.value = previousValue;
    }

    // Update delete button visibility.
    const chatDeleteBtn = document.getElementById('chatDeleteConvoBtn');
    if (chatDeleteBtn) {
        const selectedInfo = convos.find(c => c.id === dropdown.value);
        chatDeleteBtn.style.display = (selectedInfo && selectedInfo.isFinished) ? '' : 'none';
    }
}

function updateDetailConversationDropdown(ticketId) {
    const dropdown = document.getElementById('detailConversationDropdown');
    if (!dropdown) {
        return;
    }

    const convos = sortConversations(ticketConversations[ticketId] || []);
    const previousValue = dropdown.value;

    dropdown.innerHTML = '<option value="">💬 Select conversation…</option>';
    for (let i = 0; i < convos.length; i++) {
        const c = convos[i];
        const suffix = c.isFinished ? ' ✓' : ` (${c.messageCount})`;
        const option = document.createElement('option');
        option.value = c.id;
        option.textContent = c.displayName + suffix;
        dropdown.appendChild(option);
    }

    if (previousValue && convos.some(c => c.id === previousValue)) {
        dropdown.value = previousValue;
    }

    // Update delete button visibility.
    const detailDeleteBtn = document.getElementById('detailDeleteConvoBtn');
    if (detailDeleteBtn) {
        const selectedInfo = convos.find(c => c.id === dropdown.value);
        detailDeleteBtn.style.display = (selectedInfo && selectedInfo.isFinished) ? '' : 'none';
    }
}

function openChat(ticketId) {
    const modal = document.getElementById('chatModal');
    const messagesDiv = document.getElementById('chatMessages');
    const titleEl = document.getElementById('chatTitle');
    titleEl.textContent = `💬 Conversations — #${ticketId}`;

    // If opening from detail modal, preserve the selected conversation.
    const selectedConvId = (detailChatTicketId === ticketId) ? detailChatConversationId : null;

    chatModalTicketId = ticketId;
    chatModalConversationId = selectedConvId;
    ticketPendingToolCalls[ticketId] = {};

    // Build dropdown.
    const dropdownContainer = document.getElementById('chatConversationPicker');
    dropdownContainer.innerHTML = '';

    // Add planner LLM selector.
    const ticket = tickets.find(t => t.id === ticketId);
    if (ticket) {
        const plannerHtml = buildPlannerLlmDropdown(ticket, 'chatPlannerLlm');
        if (plannerHtml) {
            const plannerWrapper = document.createElement('span');
            plannerWrapper.innerHTML = plannerHtml;
            dropdownContainer.appendChild(plannerWrapper.firstElementChild);
        }
    }

    const dropdown = document.createElement('select');
    dropdown.id = 'conversationDropdown';
    dropdown.className = 'chat-conversation-select';
    dropdown.innerHTML = '<option value="">💬 Select conversation…</option>';

    const convos = sortConversations(ticketConversations[ticketId] || []);
    for (let i = 0; i < convos.length; i++) {
        const c = convos[i];
        const suffix = c.isFinished ? ' ✓' : ` (${c.messageCount})`;
        const option = document.createElement('option');
        option.value = c.id;
        option.textContent = c.displayName + suffix;
        dropdown.appendChild(option);
    }

    // Set initial selection.
    if (selectedConvId && convos.some(c => c.id === selectedConvId)) {
        dropdown.value = selectedConvId;
        loadAndDisplayConversation(ticketId, selectedConvId);
        const info = convos.find(c => c.id === selectedConvId);
        setChatInputEnabled(!(info && info.isFinished));
        updateBusyIndicators(ticketId, selectedConvId, !!busyConversations[`${ticketId}:${selectedConvId}`]);
    } else {
        messagesDiv.innerHTML = '<div class="chat-msg chat-msg-system">Select a conversation from the dropdown above.</div>';
        setChatInputEnabled(false);
    }

    dropdown.addEventListener('change', () => {
        const convId = dropdown.value;
        if (convId) {
            chatModalConversationId = convId;
            loadAndDisplayConversation(ticketId, convId);
            const info = convos.find(c => c.id === convId);
            setChatInputEnabled(!(info && info.isFinished));
            updateBusyIndicators(ticketId, convId, !!busyConversations[`${ticketId}:${convId}`]);
        } else {
            chatModalConversationId = null;
            messagesDiv.innerHTML = '<div class="chat-msg chat-msg-system">Select a conversation from the dropdown above.</div>';
            setChatInputEnabled(false);
            updateBusyIndicators(ticketId, '', false);
        }
    });

    dropdownContainer.appendChild(dropdown);

    const deleteBtn = document.createElement('button');
    deleteBtn.id = 'chatDeleteConvoBtn';
    deleteBtn.className = 'detail-chat-maximize';
    deleteBtn.title = 'Delete finished conversation';
    deleteBtn.textContent = '🗑️';
    deleteBtn.style.display = 'none';
    deleteBtn.addEventListener('click', () => {
        deleteConversation(ticketId, dropdown.value);
    });
    dropdownContainer.appendChild(deleteBtn);

    // Show/hide delete button based on selection.
    function updateDeleteButton() {
        const convId = dropdown.value;
        const info = convos.find(c => c.id === convId);
        deleteBtn.style.display = (info && info.isFinished) ? '' : 'none';
    }
    dropdown.addEventListener('change', updateDeleteButton);
    updateDeleteButton();

    // Subscribe to this ticket's group for real-time updates.
    if (connection && connection.state === signalR.HubConnectionState.Connected) {
        connection.invoke('SubscribeToTicket', ticketId).catch(() => {});
    }

    modal.classList.add('active');

    const input = document.getElementById('chatInput');
    input.value = '';
}

window.openChat = openChat;

function closeChatModal() {
    const ticketId = chatModalTicketId;
    const modal = document.getElementById('chatModal');
    modal.classList.remove('active');
    chatModalTicketId = null;
    chatModalConversationId = null;

    if (ticketId && connection && connection.state === signalR.HubConnectionState.Connected) {
        connection.invoke('UnsubscribeFromTicket', ticketId).catch(() => {});
    }
}

function setChatInputEnabled(enabled) {
    const input = document.getElementById('chatInput');
    const btn = document.getElementById('chatSendBtn');
    if (input) {
        input.disabled = !enabled;
        input.placeholder = enabled ? 'Type a message…' : 'Conversation finished';
    }
    if (btn) {
        btn.disabled = !enabled;
    }
}

async function loadAndDisplayConversation(ticketId, conversationId) {
    const messagesDiv = document.getElementById('chatMessages');
    ticketPendingToolCalls[ticketId] = {};

    const key = `${ticketId}:${conversationId}`;
    let msgs = conversationMessages[key];

    // Fetch from server if not cached.
    if (!msgs) {
        try {
            const response = await fetch(`${API_BASE}/tickets/${ticketId}/conversations/${conversationId}`);
            if (response.ok) {
                const data = await response.json();
                msgs = data.messages || [];
                conversationMessages[key] = msgs;
            } else {
                msgs = [];
            }
        } catch {
            msgs = [];
        }
    }

    messagesDiv.innerHTML = '';
    for (let i = 0; i < msgs.length; i++) {
        renderSingleChatMessage(msgs, i);
    }
}

function renderSingleChatMessage(messages, index) {
    const msg = messages[index];
    const messagesDiv = document.getElementById('chatMessages');

    if (msg.role === 'user') {
        const content = (msg.content || '');
        appendChatUser(content);
    } else if (msg.role === 'assistant') {
        if (msg.content) {
            appendChatAssistant(msg.content);
        }
        if (msg.tool_calls) {
            for (let i = 0; i < msg.tool_calls.length; i++) {
                const tc = msg.tool_calls[i];
                // Look ahead for the matching tool result
                let result = null;
                for (let j = index + 1; j < messages.length; j++) {
                    if (messages[j].role === 'tool' && messages[j].tool_call_id === tc.id) {
                        result = messages[j].content || '';
                        break;
                    }
                }
                appendChatToolCall(messagesDiv, tc.function.name, tc.function.arguments, result);
            }
        }
    } else if (msg.role === 'system') {
        appendChatSystem(msg.content || '');
    }
    // Skip role === 'tool' — results are embedded in tool call accordions
}

function appendChatAssistant(content) {
    const messagesDiv = document.getElementById('chatMessages');
    const el = document.createElement('div');
    el.className = 'chat-msg chat-msg-assistant';
    el.innerHTML = renderMarkdown(content);

    messagesDiv.appendChild(el);
    scrollChatToBottom();
}

function appendChatUser(content) {
    const messagesDiv = document.getElementById('chatMessages');
    const el = document.createElement('div');
    el.className = 'chat-msg chat-msg-user';
    el.textContent = content;

    messagesDiv.appendChild(el);
    scrollChatToBottom();
}

function appendChatSystem(content) {
    const messagesDiv = document.getElementById('chatMessages');
    const el = document.createElement('div');
    el.className = 'chat-msg chat-msg-system';
    el.textContent = content;

    messagesDiv.appendChild(el);
    scrollChatToBottom();
}

function appendChatToolCall(container, name, argsJson, result) {
    const args = formatToolCallParams(argsJson);
    const el = document.createElement('div');
    el.className = 'chat-tool-accordion';

    const resultHtml = result ? `
        <div class="chat-tool-section chat-tool-result">
            <pre>${escapeHtml(result)}</pre>
        </div>
    ` : '';

    el.innerHTML = `
        <div class="chat-tool-header">
            <span class="chat-tool-icon">▶</span>
            <span class="chat-tool-name">${escapeHtml(name)}</span>
            <span class="chat-tool-params">${escapeHtml(args)}</span>
        </div>
        <div class="chat-tool-body">
            ${resultHtml}
        </div>
    `;

    el.querySelector('.chat-tool-header').addEventListener('click', () => {
        const icon = el.querySelector('.chat-tool-icon');
        const body = el.querySelector('.chat-tool-body');
        const isOpen = body.classList.contains('open');

        if (isOpen) {
            body.classList.remove('open');
            icon.classList.remove('open');
        } else {
            body.classList.add('open');
            icon.classList.add('open');
        }
    });

    container.appendChild(el);
    scrollChatToBottom();
}

function truncateToolLine(name, args) {
    try {
        const obj = JSON.parse(args);
        const keys = Object.keys(obj);
        if (keys.length > 0) {
            const firstVal = String(obj[keys[0]]);
            return firstVal.length > 80 ? firstVal.substring(0, 80) + '…' : firstVal;
        }
    } catch {
        // Not valid JSON
    }

    if (args && args.length > 80) {
        return args.substring(0, 80) + '…';
    }

    return args || '';
}

function truncateToolResult(result) {
    if (result.length > 2000) {
        return result.substring(0, 2000) + '\n… (truncated)';
    }

    return result;
}

function formatJsonSafe(jsonStr) {
    try {
        return JSON.stringify(JSON.parse(jsonStr), null, 2);
    } catch {
        return jsonStr;
    }
}

function renderMarkdown(text) {
    if (!text) {
        return '';
    }

    let html = escapeHtml(text);

    // Code blocks
    html = html.replace(/```(\w*)\n([\s\S]*?)```/g, '<pre><code>$2</code></pre>');

    // Inline code
    html = html.replace(/`([^`]+)`/g, '<code>$1</code>');

    // Bold
    html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');

    // Italic
    html = html.replace(/\*([^*]+)\*/g, '<em>$1</em>');

    return html;
}

function sendChatMessage() {
    const input = document.getElementById('chatInput');
    const text = input.value.trim();

    if (!text || !chatModalTicketId || !chatModalConversationId) {
        return;
    }

    if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
        appendChatSystem('Not connected to server.');
        return;
    }

    connection.invoke('SendChatToWorker', chatModalTicketId, chatModalConversationId, text).catch(err => {
        appendChatSystem(`Failed to send: ${err.message}`);
    });
    input.value = '';
    autoResizeChatInput();
    input.focus();
}

function scrollChatToBottom() {
    const messagesDiv = document.getElementById('chatMessages');
    requestAnimationFrame(() => {
        messagesDiv.scrollTop = messagesDiv.scrollHeight;
    });
}

// Reloads the full-screen chat conversation while preserving scroll position.
// If the user was scrolled to the bottom, it stays at the bottom after reload.
async function reloadConversationPreservingScroll(ticketId, conversationId) {
    const messagesDiv = document.getElementById('chatMessages');
    if (!messagesDiv) return;

    const atBottom = messagesDiv.scrollHeight - messagesDiv.scrollTop - messagesDiv.clientHeight < 40;
    const previousScroll = messagesDiv.scrollTop;

    await loadAndDisplayConversation(ticketId, conversationId);

    requestAnimationFrame(() => {
        if (atBottom) {
            messagesDiv.scrollTop = messagesDiv.scrollHeight;
        } else {
            messagesDiv.scrollTop = previousScroll;
        }
    });
}

// Reloads the inline detail chat conversation while preserving scroll position.
async function reloadDetailConversationPreservingScroll(ticketId, conversationId) {
    const messagesDiv = document.getElementById('detailChatMessages');
    if (!messagesDiv) return;

    const atBottom = messagesDiv.scrollHeight - messagesDiv.scrollTop - messagesDiv.clientHeight < 40;
    const previousScroll = messagesDiv.scrollTop;

    await loadAndDisplayDetailConversation(ticketId, conversationId);

    requestAnimationFrame(() => {
        if (atBottom) {
            messagesDiv.scrollTop = messagesDiv.scrollHeight;
            saveTicketConversationState(ticketId, conversationId, { atBottom: true });
        } else {
            messagesDiv.scrollTop = previousScroll;
            saveTicketConversationState(ticketId, conversationId, { messageIndex: messagesDiv.children.length - 1 });
        }
    });
}

function autoResizeChatInput() {
    const input = document.getElementById('chatInput');
    input.style.height = 'auto';
    input.style.height = Math.min(input.scrollHeight, 200) + 'px';
}

function setupChatEventListeners() {
    const input = document.getElementById('chatInput');
    const sendBtn = document.getElementById('chatSendBtn');
    const closeBtn = document.getElementById('chatCloseBtn');
    const modal = document.getElementById('chatModal');

    input.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendChatMessage();
        }
    });

    input.addEventListener('input', autoResizeChatInput);

    sendBtn.addEventListener('click', () => {
        sendChatMessage();
    });

    closeBtn.addEventListener('click', () => {
        closeChatModal();
    });

    modal.addEventListener('click', (e) => {
        if (e.target === modal) {
            closeChatModal();
        }
    });
}

// Utility functions

// Updates busy spinner and stop button state for a conversation.
function updateBusyIndicators(ticketId, conversationId, isBusy) {
    // Full-screen chat modal.
    if (chatModalTicketId === ticketId && chatModalConversationId === conversationId) {
        const messagesDiv = document.getElementById('chatMessages');
        const stopBtn = document.getElementById('chatStopBtn');
        if (isBusy) {
            appendBusySpinner(messagesDiv, 'chatBusySpinner');
            if (stopBtn) {
                stopBtn.classList.add('active');
            }
        } else {
            removeBusySpinner(messagesDiv, 'chatBusySpinner');
            if (stopBtn) {
                stopBtn.classList.remove('active');
            }
        }
    }

    // Inline detail chat.
    if (detailChatTicketId === ticketId && detailChatConversationId === conversationId) {
        const messagesDiv = document.getElementById('detailChatMessages');
        const stopBtn = document.getElementById('detailChatStopBtn');
        if (isBusy) {
            appendBusySpinner(messagesDiv, 'detailBusySpinner');
            if (stopBtn) {
                stopBtn.classList.add('active');
            }
        } else {
            removeBusySpinner(messagesDiv, 'detailBusySpinner');
            if (stopBtn) {
                stopBtn.classList.remove('active');
            }
        }
    }
}

function appendBusySpinner(container, spinnerId) {
    if (!container || document.getElementById(spinnerId)) {
        return;
    }
    const el = document.createElement('div');
    el.id = spinnerId;
    el.className = 'chat-busy-spinner';
    el.innerHTML = '<span class="spinner-dots"><span>.</span><span>.</span><span>.</span></span>';
    container.appendChild(el);
    requestAnimationFrame(() => {
        container.scrollTop = container.scrollHeight;
    });
}

function removeBusySpinner(container, spinnerId) {
    if (!container) {
        return;
    }
    const spinner = document.getElementById(spinnerId);
    if (spinner) {
        spinner.remove();
    }
}

function interruptConversation() {
    // Determine which conversation to interrupt based on which chat is active.
    let ticketId = chatModalTicketId;
    let conversationId = chatModalConversationId;
    if (!ticketId || !conversationId) {
        ticketId = detailChatTicketId;
        conversationId = detailChatConversationId;
    }
    if (!ticketId || !conversationId) {
        return;
    }

    if (connection && connection.state === signalR.HubConnectionState.Connected) {
        connection.invoke('RequestInterruptConversation', ticketId, conversationId).catch(err => {
            console.error('Failed to interrupt:', err);
        });
    }
}

window.interruptConversation = interruptConversation;

function escapeHtml(text) {
    if (text == null) {
        return '';
    }

    const div = document.createElement('div');
    div.textContent = String(text);

    return div.innerHTML;
}

// Initialize on DOM ready
document.addEventListener('DOMContentLoaded', () => {
    init();
    setupChatEventListeners();
});
