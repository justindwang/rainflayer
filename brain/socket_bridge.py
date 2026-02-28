"""Socket Bridge for Python↔C# RoR2 Mod communication.

Protocol:
- JSON messages delimited by newlines (\n)
- Python → C#:  {"type": "QUERY_INVENTORY"}  (queries/commands)
- C# → Python:  {"type": "INVENTORY", ...}   (query responses)
- C# → Python:  {"type": "EVENT", "event_type": "action_started", ...}  (unsolicited events)

A background reader thread routes every incoming line:
  - type == "EVENT"  →  _event_queue   (brain drains this each cycle via poll_events())
  - anything else    →  _response_queue (matched by the pending send_query() call)
"""
import json
import logging
import queue
import socket
import threading
from typing import Any, Dict, List, Optional

logger = logging.getLogger(__name__)


class SocketBridge:
    """
    TCP socket server for communicating with RoR2 C# mod.

    Python runs as server, C# mod connects as client.
    A background reader thread handles all incoming data so events and
    query responses never block or interfere with each other.
    """

    def __init__(self, host: str = "127.0.0.1", port: int = 7777):
        self.host = host
        self.port = port
        self.server_socket: Optional[socket.socket] = None
        self.client_socket: Optional[socket.socket] = None
        self.connected = False

        # Serialise sends so concurrent callers don't interleave bytes on the wire
        self._send_lock = threading.Lock()

        # Reader thread routes messages into one of two queues
        self._response_queue: queue.Queue = queue.Queue()
        self._event_queue: queue.Queue = queue.Queue(maxsize=200)
        self._reader_thread: Optional[threading.Thread] = None

    # ------------------------------------------------------------------
    # Connection lifecycle
    # ------------------------------------------------------------------

    def start(self, blocking: bool = True, timeout: float = None):
        """
        Start the socket server.

        Args:
            blocking: If True, block until first client connects. If False, accept in background.
            timeout: If blocking, maximum seconds to wait for first connection (None = infinite)
        """
        self.server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.server_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.server_socket.bind((self.host, self.port))
        self.server_socket.listen(1)

        logger.info(f"[SocketBridge] Listening on {self.host}:{self.port}")
        logger.info("[SocketBridge] Waiting for C# mod to connect...")

        if blocking:
            if timeout:
                self.server_socket.settimeout(timeout)
            try:
                self.client_socket, addr = self.server_socket.accept()
                self.server_socket.settimeout(None)  # reset for future reconnects
                self.connected = True
                logger.info(f"[SocketBridge] Connected to C# mod from {addr}")
                self._start_reader()
                self._start_accept_loop()  # keep accepting future reconnects
            except socket.timeout:
                logger.warning(f"[SocketBridge] Timeout waiting for connection after {timeout}s")
                self.server_socket.close()
                self.server_socket = None
                raise TimeoutError(f"Timeout waiting for connection on {self.host}:{self.port}")
        else:
            self._start_accept_loop()  # handles all accepts (first connect + reconnects)

    def stop(self):
        """Stop the socket server and reader thread."""
        self.connected = False

        if self.client_socket:
            try:
                self.client_socket.close()
            except Exception:
                pass
            self.client_socket = None

        if self.server_socket:
            try:
                self.server_socket.close()
            except Exception:
                pass
            self.server_socket = None

        logger.info("[SocketBridge] Stopped")

    # ------------------------------------------------------------------
    # Background accept loop (handles initial connect + all reconnects)
    # ------------------------------------------------------------------

    def _start_accept_loop(self):
        t = threading.Thread(target=self._accept_loop, daemon=True, name="SocketBridge-Accept")
        t.start()

    def _accept_loop(self):
        """Persistent loop: accept a connection, wait for disconnect, repeat."""
        import time
        while self.server_socket is not None:
            if self.connected:
                time.sleep(0.5)
                continue

            logger.info("[SocketBridge] Waiting for C# mod to connect (or reconnect)...")
            try:
                self.server_socket.settimeout(None)
                client, addr = self.server_socket.accept()

                # Close stale client socket from previous session
                if self.client_socket:
                    try:
                        self.client_socket.close()
                    except Exception:
                        pass

                # Flush stale queue entries from the previous session
                for q in (self._response_queue, self._event_queue):
                    while not q.empty():
                        try:
                            q.get_nowait()
                        except Exception:
                            break

                self.client_socket = client
                self.connected = True
                logger.info(f"[SocketBridge] Connected to C# mod from {addr}")
                self._start_reader()

            except OSError as e:
                if self.server_socket is None:
                    break  # stop() was called
                logger.error(f"[SocketBridge] Accept error: {e}")
                time.sleep(1.0)

    # ------------------------------------------------------------------
    # Background reader
    # ------------------------------------------------------------------

    def _start_reader(self):
        """Launch the background reader thread. Called once after connection."""
        self._reader_thread = threading.Thread(
            target=self._reader_loop, daemon=True, name="SocketBridge-Reader"
        )
        self._reader_thread.start()
        logger.info("[SocketBridge] Reader thread started")

    def _reader_loop(self):
        """
        Background thread: read every newline-delimited JSON message C# sends.

        Routes:
          {"type": "EVENT", ...}  →  _event_queue   (brain polls each cycle)
          anything else           →  _response_queue (consumed by matching send_query)
        """
        try:
            sock_file = self.client_socket.makefile('rb')
            while self.connected:
                line = sock_file.readline()
                if not line:
                    logger.info("[SocketBridge] Connection closed by C# mod")
                    self.connected = False
                    break

                line_str = line.decode('utf-8-sig').strip()
                if not line_str:
                    continue

                try:
                    msg = json.loads(line_str)
                except json.JSONDecodeError as e:
                    logger.error(f"[SocketBridge] JSON parse error: {e} — line: {line_str[:120]}")
                    continue

                if msg.get("type") == "EVENT":
                    try:
                        self._event_queue.put_nowait(msg)
                    except queue.Full:
                        # Drop oldest to make room for newest
                        try:
                            self._event_queue.get_nowait()
                            self._event_queue.put_nowait(msg)
                        except queue.Empty:
                            pass
                else:
                    self._response_queue.put(msg)

        except Exception as e:
            if self.connected:
                logger.error(f"[SocketBridge] Reader thread error: {e}")
            self.connected = False

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    def send_query(self, query_type: str, **kwargs) -> Optional[Dict[str, Any]]:
        """
        Send a query to C# mod and wait for its response.

        Thread-safe: serialises sends via _send_lock, reads from _response_queue
        (populated by the background reader thread).

        Args:
            query_type: Type of query (e.g., "QUERY_INVENTORY")
            **kwargs: Additional query parameters

        Returns:
            Response dict from C# mod, or None if error/timeout
        """
        if not self.connected or not self.client_socket:
            logger.debug(f"[SocketBridge] Not connected, can't send query: {query_type}")
            return None

        try:
            message = json.dumps({"type": query_type, **kwargs}) + "\n"
            with self._send_lock:
                self.client_socket.sendall(message.encode('utf-8'))

            # Block until reader thread puts the matching response in the queue
            response = self._response_queue.get(timeout=5.0)
            return response

        except queue.Empty:
            logger.error(f"[SocketBridge] Timeout waiting for response to {query_type}")
            return None
        except Exception as e:
            logger.error(f"[SocketBridge] Error in send_query({query_type}): {e}")
            self.connected = False
            return None

    def poll_events(self) -> List[Dict[str, Any]]:
        """
        Non-blocking drain of the event queue.

        Call once per brain cycle to collect all pending C# events.
        Each event dict has at minimum: {"type": "EVENT", "event_type": "..."}

        Returns:
            List of event dicts (may be empty).
        """
        events = []
        while True:
            try:
                events.append(self._event_queue.get_nowait())
            except queue.Empty:
                break
        return events

    def is_connected(self) -> bool:
        """Check if connected to C# mod."""
        return self.connected


# Global socket bridge instance
_socket_bridge: Optional[SocketBridge] = None


def get_socket_bridge() -> Optional[SocketBridge]:
    """Get the global socket bridge instance."""
    return _socket_bridge


def start_socket_bridge(host: str = "127.0.0.1", port: int = 7777, blocking: bool = False, timeout: float = None) -> SocketBridge:
    """
    Start and return the global socket bridge.

    If a bridge is already running and connected, returns it as-is without
    restarting (so the REPL and orchestrator can share the same connection).
    """
    global _socket_bridge

    if _socket_bridge and _socket_bridge.is_connected():
        return _socket_bridge

    if _socket_bridge:
        stop_socket_bridge()

    _socket_bridge = SocketBridge(host, port)
    _socket_bridge.start(blocking=blocking, timeout=timeout)
    return _socket_bridge


def stop_socket_bridge():
    """Stop the global socket bridge."""
    global _socket_bridge
    if _socket_bridge:
        _socket_bridge.stop()
        _socket_bridge = None


if __name__ == "__main__":
    import time
    logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(name)s - %(levelname)s - %(message)s')

    print("[SocketBridge] Starting socket server on 127.0.0.1:7777")
    print("[SocketBridge] Start RoR2 with the mod to test connection")

    bridge = SocketBridge()
    bridge.start()

    try:
        while True:
            time.sleep(5)
            if bridge.is_connected():
                print("\n[SocketBridge] Sending test query...")
                response = bridge.send_query("QUERY_INVENTORY")
                if response:
                    print(f"[SocketBridge] Got response: {response}")
                else:
                    print("[SocketBridge] No response or error")

                events = bridge.poll_events()
                if events:
                    print(f"[SocketBridge] Pending events: {events}")
    except KeyboardInterrupt:
        print("\n[SocketBridge] Stopping...")
        bridge.stop()
