"""
TCP Terminal - receives data over TCP/IP and shows it in a command-prompt style console.

Run in VS Code:  python tcp_terminal.py
Needs only Python 3 (tkinter comes built in on Windows).

Modes:
  Client : connects to an IP + Port (device / server sends you data)
  Server : listens on a Port (device connects to you and sends data)
"""

import queue
import socket
import threading
import tkinter as tk
from datetime import datetime
from tkinter import ttk


class TcpTerminal(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("TCP Terminal")
        self.geometry("820x560")
        self.minsize(640, 400)

        self.q = queue.Queue()          # messages from threads -> UI
        self.sock = None                # active data socket
        self.server_sock = None         # listening socket (server mode)
        self.running = False

        self._build_ui()
        self.after(50, self._drain_queue)
        self.protocol("WM_DELETE_WINDOW", self._on_close)

    # ---------------- UI ----------------
    def _build_ui(self):
        top = ttk.Frame(self, padding=8)
        top.pack(fill="x")

        ttk.Label(top, text="Mode").grid(row=0, column=0, padx=(0, 4))
        self.mode = tk.StringVar(value="Client")
        mode_box = ttk.Combobox(top, textvariable=self.mode, width=8,
                                values=["Client", "Server"], state="readonly")
        mode_box.grid(row=0, column=1, padx=(0, 12))
        mode_box.bind("<<ComboboxSelected>>", self._mode_changed)

        ttk.Label(top, text="IP Address").grid(row=0, column=2, padx=(0, 4))
        self.ip = tk.StringVar(value="127.0.0.1")
        self.ip_entry = ttk.Entry(top, textvariable=self.ip, width=18)
        self.ip_entry.grid(row=0, column=3, padx=(0, 12))

        ttk.Label(top, text="Port").grid(row=0, column=4, padx=(0, 4))
        self.port = tk.StringVar(value="5000")
        ttk.Entry(top, textvariable=self.port, width=8).grid(row=0, column=5, padx=(0, 12))

        self.connect_btn = ttk.Button(top, text="Connect", command=self.start)
        self.connect_btn.grid(row=0, column=6, padx=(0, 6))
        self.stop_btn = ttk.Button(top, text="Disconnect", command=self.stop, state="disabled")
        self.stop_btn.grid(row=0, column=7, padx=(0, 6))
        ttk.Button(top, text="Clear", command=self.clear).grid(row=0, column=8)

        # Console (cmd-prompt look)
        mid = ttk.Frame(self, padding=(8, 0))
        mid.pack(fill="both", expand=True)
        self.console = tk.Text(mid, bg="#0c0c0c", fg="#cccccc", insertbackground="white",
                               font=("Consolas", 11), wrap="word", state="disabled")
        sb = ttk.Scrollbar(mid, command=self.console.yview)
        self.console.configure(yscrollcommand=sb.set)
        sb.pack(side="right", fill="y")
        self.console.pack(side="left", fill="both", expand=True)
        self.console.tag_config("rx", foreground="#00ff7f")
        self.console.tag_config("tx", foreground="#4fc3f7")
        self.console.tag_config("info", foreground="#ffd54f")
        self.console.tag_config("err", foreground="#ff5252")

        # Send box
        bottom = ttk.Frame(self, padding=8)
        bottom.pack(fill="x")
        self.send_var = tk.StringVar()
        self.send_entry = ttk.Entry(bottom, textvariable=self.send_var)
        self.send_entry.pack(side="left", fill="x", expand=True, padx=(0, 6))
        self.send_entry.bind("<Return>", lambda e: self.send())
        self.add_crlf = tk.BooleanVar(value=True)
        ttk.Checkbutton(bottom, text="Add \\r\\n", variable=self.add_crlf).pack(side="left", padx=6)
        ttk.Button(bottom, text="Send", command=self.send).pack(side="left")

        self.status = tk.StringVar(value="Disconnected")
        ttk.Label(self, textvariable=self.status, anchor="w", padding=(8, 2)).pack(fill="x")

    def _mode_changed(self, _=None):
        if self.mode.get() == "Server":
            self.ip.set("0.0.0.0")
            self.connect_btn.config(text="Listen")
        else:
            self.ip.set("127.0.0.1")
            self.connect_btn.config(text="Connect")

    # ---------------- console helpers ----------------
    def log(self, text, tag=None, stamp=True):
        self.console.configure(state="normal")
        prefix = f"[{datetime.now():%H:%M:%S}] " if stamp else ""
        self.console.insert("end", prefix + text, tag)
        self.console.see("end")
        self.console.configure(state="disabled")

    def clear(self):
        self.console.configure(state="normal")
        self.console.delete("1.0", "end")
        self.console.configure(state="disabled")

    # ---------------- connection control ----------------
    def start(self):
        try:
            port = int(self.port.get())
            if not 0 < port < 65536:
                raise ValueError
        except ValueError:
            self.log("Invalid port (use 1-65535)\n", "err")
            return
        ip = self.ip.get().strip()
        self.running = True
        self.connect_btn.config(state="disabled")
        self.stop_btn.config(state="normal")
        target = self._client_thread if self.mode.get() == "Client" else self._server_thread
        threading.Thread(target=target, args=(ip, port), daemon=True).start()

    def stop(self):
        self.running = False
        for s in (self.sock, self.server_sock):
            try:
                if s:
                    s.close()
            except OSError:
                pass
        self.sock = self.server_sock = None
        self.connect_btn.config(state="normal")
        self.stop_btn.config(state="disabled")
        self.status.set("Disconnected")
        self.log("Disconnected\n", "info")

    # ---------------- worker threads ----------------
    def _client_thread(self, ip, port):
        self.q.put(("status", f"Connecting to {ip}:{port} ..."))
        try:
            s = socket.create_connection((ip, port), timeout=5)
            s.settimeout(None)
        except OSError as e:
            self.q.put(("err", f"Connection failed: {e}\n"))
            self.q.put(("stop", None))
            return
        self.sock = s
        self.q.put(("info", f"Connected to {ip}:{port}\n"))
        self.q.put(("status", f"Connected to {ip}:{port}"))
        self._receive_loop(s)

    def _server_thread(self, ip, port):
        try:
            srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            srv.bind((ip, port))
            srv.listen(1)
        except OSError as e:
            self.q.put(("err", f"Cannot listen: {e}\n"))
            self.q.put(("stop", None))
            return
        self.server_sock = srv
        self.q.put(("info", f"Listening on {ip}:{port} ...\n"))
        self.q.put(("status", f"Listening on {ip}:{port}"))
        while self.running:
            try:
                conn, addr = srv.accept()
            except OSError:
                break
            self.sock = conn
            self.q.put(("info", f"Client connected: {addr[0]}:{addr[1]}\n"))
            self.q.put(("status", f"Client {addr[0]}:{addr[1]} connected"))
            self._receive_loop(conn)
            if self.running:
                self.q.put(("info", "Client disconnected, waiting for next ...\n"))
                self.q.put(("status", f"Listening on {ip}:{port}"))

    def _receive_loop(self, s):
        while self.running:
            try:
                data = s.recv(4096)
            except OSError:
                break
            if not data:
                break
            self.q.put(("rx", data.decode("utf-8", errors="replace")))
        if self.mode.get() == "Client" and self.running:
            self.q.put(("info", "Connection closed by remote\n"))
            self.q.put(("stop", None))

    # ---------------- send ----------------
    def send(self):
        text = self.send_var.get()
        if not text or not self.sock:
            if not self.sock:
                self.log("Not connected\n", "err")
            return
        payload = text + ("\r\n" if self.add_crlf.get() else "")
        try:
            self.sock.sendall(payload.encode("utf-8"))
            self.log(f"TX: {text}\n", "tx")
            self.send_var.set("")
        except OSError as e:
            self.log(f"Send failed: {e}\n", "err")

    # ---------------- UI queue ----------------
    def _drain_queue(self):
        try:
            while True:
                kind, val = self.q.get_nowait()
                if kind == "rx":
                    self.log("RX: " + val + ("" if val.endswith("\n") else "\n"), "rx")
                elif kind == "info":
                    self.log(val, "info")
                elif kind == "err":
                    self.log(val, "err")
                elif kind == "status":
                    self.status.set(val)
                elif kind == "stop":
                    self.running = False
                    self.connect_btn.config(state="normal")
                    self.stop_btn.config(state="disabled")
                    self.status.set("Disconnected")
        except queue.Empty:
            pass
        self.after(50, self._drain_queue)

    def _on_close(self):
        self.running = False
        for s in (self.sock, self.server_sock):
            try:
                if s:
                    s.close()
            except OSError:
                pass
        self.destroy()


if __name__ == "__main__":
    TcpTerminal().mainloop()
