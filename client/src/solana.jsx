import { SolanaProvider } from '@lifi/widget-provider-solana';
import { register } from './core.jsx';

register('solana', () => [SolanaProvider()]);
