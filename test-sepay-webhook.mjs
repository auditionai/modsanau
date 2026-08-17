// Test SePay webhook signature verification
import crypto from 'crypto';

const WEBHOOK_URL = 'https://plvuutsjwsawkkrmvigz.supabase.co/functions/v1/sepay-webhook';
const SECRET = '3fd6c7400ade45d6c4dc11636669fb51b9d6093f6adbf896724e3e35';

async function testWebhook() {
  const payload = {
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
  };

  const body = JSON.stringify(payload);
  const timestamp = Math.floor(Date.now() / 1000).toString();

  // SePay standard: HMAC-SHA256(timestamp + "." + body)
  const message = `${timestamp}.${body}`;
  const hmac = crypto.createHmac('sha256', SECRET);
  hmac.update(message);
  const signature = `sha256=${hmac.digest('hex')}`;

  console.log('Testing SePay webhook with standard format:');
  console.log('- Timestamp:', timestamp);
  console.log('- Signature:', signature);
  console.log('- Message format: timestamp + "." + body');
  console.log('- Signature format: "sha256=" + hex');

  const response = await fetch(WEBHOOK_URL, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Content-Length': body.length.toString(),
      'x-sepay-timestamp': timestamp,
      'x-sepay-signature': signature
    },
    body
  });

  const result = await response.json();
  console.log('\nResponse:', response.status, result);

  if (response.status === 401) {
    console.log('\n❌ FAILED: Webhook rejected signature');
    console.log('   → Code không tương thích với SePay standard');
  } else if (response.status === 500 && result.error === 'WEBHOOK_PERSISTENCE_FAILED') {
    console.log('\n✅ PASSED: Signature accepted (500 = persistence issue, not auth)');
  } else if (response.status === 200) {
    console.log('\n✅ PASSED: Webhook processed successfully');
  } else {
    console.log('\n⚠️  UNKNOWN:', response.status, result);
  }
}

testWebhook().catch(console.error);
