// Debug: Test HMAC computation locally
import crypto from 'crypto';

const SECRET = '3fd6c7400ade45d6c4dc11636669fb51b9d6093f6adbf896724e3e35';
const body = JSON.stringify({
  id: '12345',
  gateway: 'MBBANK',
  transactionDate: '2026-08-16 10:30:00',
  accountNumber: '0824280497',
  subAccountNumber: '',
  transferType: 'in',
  transferAmount: 50000,
  accumulated: 0,
  content: 'Thanh toan AMS20260816ABCD',
  code: 'AMS20260816ABCD',
  description: '',
  referenceCode: 'REF123',
  body: ''
});

const timestamp = '1786818000';

// Method 1: timestamp.body (wrong)
const msg1 = timestamp + body;
const hmac1 = crypto.createHmac('sha256', SECRET);
hmac1.update(msg1);
const sig1 = `sha256=${hmac1.digest('hex')}`;

// Method 2: timestamp.body (correct - with dot)
const msg2 = `${timestamp}.${body}`;
const hmac2 = crypto.createHmac('sha256', SECRET);
hmac2.update(msg2);
const sig2 = `sha256=${hmac2.digest('hex')}`;

console.log('Method 1 (no dot):');
console.log('  Message:', msg1.substring(0, 50) + '...');
console.log('  Signature:', sig1);
console.log('');
console.log('Method 2 (with dot):');
console.log('  Message:', msg2.substring(0, 50) + '...');
console.log('  Signature:', sig2);
console.log('');
console.log('Testing both methods against webhook...');

async function test(sig, label) {
  const res = await fetch('https://plvuutsjwsawkkrmvigz.supabase.co/functions/v1/sepay-webhook', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'x-sepay-timestamp': timestamp,
      'x-sepay-signature': sig
    },
    body
  });
  const result = await res.json();
  console.log(`${label}: ${res.status} ${JSON.stringify(result)}`);
}

await test(sig1, 'Method 1 (no dot)');
await test(sig2, 'Method 2 (with dot)');
