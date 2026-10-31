import sys,struct,unittest
from pathlib import Path
sys.path.append(str(Path(__file__).resolve().parents[2]/'tools'))
from idle_fixture import packet,decode,checksum,MAC,ZM,ATT,ZOMBIE

class IdlePacketTests(unittest.TestCase):
    def test_ip_tcp_checksums_and_fields(self):
        frame=packet(ZM,ATT,ZOMBIE,40000,61000,18,seq=1,ack=90001)
        ip=frame[14:34];tcp=frame[34:]
        self.assertEqual(checksum(ip),0)
        self.assertEqual(checksum(ip[12:20]+struct.pack('!BBH',0,6,len(tcp))+tcp),0)
        parsed=decode(frame);self.assertEqual(parsed['ack'],90001);self.assertEqual(parsed['flags'],18)
        self.assertEqual(parsed['src'],ATT);self.assertEqual(frame[6:12],MAC)
    def test_non_tcp_or_truncated_frame_ignored(self):
        self.assertIsNone(decode(b'short'))
        frame=bytearray(packet(ZM,ATT,ZOMBIE,40000,61000,18));frame[23]=17
        self.assertIsNone(decode(frame))
