"""Wake-on-LAN magic packets (PLAN.md S10, S16, T703).

A magic packet is six bytes ``0xFF`` followed by the MAC address of the network
adapter sixteen times, sent as a UDP datagram to the broadcast address. This module
has no Home Assistant imports: it only builds and sends the packets.
"""

import asyncio
import ipaddress
import logging
import re
import socket
from collections.abc import Iterable
from typing import Final

from .const import WOL_PORT

_LOGGER = logging.getLogger(__name__)

MAC_BYTES: Final = 6
PACKET_REPEATS: Final = 16
PACKET_LENGTH: Final = MAC_BYTES + PACKET_REPEATS * MAC_BYTES

_HEADER: Final = b"\xff" * MAC_BYTES
_MAC_SEPARATORS: Final = re.compile(r"[:\-.]")
_HEX: Final = re.compile(r"^[0-9a-fA-F]{12}$")


def normalize_mac(mac: str) -> str:
    """Return ``mac`` as ``aa:bb:cc:dd:ee:ff``.

    Accepts the usual spellings (colons, dashes, dots, none; any case). Raises
    :class:`ValueError` if the text is not a 48-bit MAC address.
    """
    digits = _MAC_SEPARATORS.sub("", mac.strip())
    if not _HEX.match(digits):
        msg = f"not a MAC address: {mac!r}"
        raise ValueError(msg)
    digits = digits.lower()
    return ":".join(digits[index : index + 2] for index in range(0, 12, 2))


def build_magic_packet(mac: str) -> bytes:
    """Return the magic packet that wakes the adapter with ``mac`` (102 bytes)."""
    raw = bytes.fromhex(normalize_mac(mac).replace(":", ""))
    return _HEADER + raw * PACKET_REPEATS


def validate_broadcast(address: str) -> str:
    """Return the IPv4 address in ``address`` in its canonical spelling.

    Raises :class:`ValueError` for anything that is not an IPv4 address.
    """
    try:
        return str(ipaddress.IPv4Address(address.strip()))
    except ValueError as err:
        msg = f"not an IPv4 address: {address!r}"
        raise ValueError(msg) from err


async def async_send_magic_packets(
    macs: Iterable[str], broadcast: str, port: int = WOL_PORT
) -> int:
    """Send a magic packet for every MAC address to ``broadcast``.

    Every address is tried even if another one fails. Returns the number of packets
    that were sent. Raises :class:`ValueError` for a MAC address or broadcast address
    that is not valid (nothing is sent then) and :class:`OSError` if the network
    refused every packet (the first error is raised).
    """
    target = validate_broadcast(broadcast)
    # One packet per adapter, however the address is spelled.
    unique = dict.fromkeys(normalize_mac(raw) for raw in macs)
    packets = [build_magic_packet(mac) for mac in unique]
    if not packets:
        return 0

    loop = asyncio.get_running_loop()
    # A plain socket instead of a datagram transport: a transport reports a failed send
    # through a protocol callback, ``sock_sendto`` raises it where it can be counted.
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sent = 0
    first_error: OSError | None = None
    try:
        sock.setblocking(False)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        for packet in packets:
            try:
                await loop.sock_sendto(sock, packet, (target, port))
            except OSError as err:
                _LOGGER.debug("Could not send a magic packet to %s: %s", target, err)
                first_error = first_error or err
            else:
                sent += 1
    finally:
        sock.close()
    if sent == 0 and first_error is not None:
        raise first_error
    return sent
