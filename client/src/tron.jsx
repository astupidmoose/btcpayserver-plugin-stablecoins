import { TronProvider } from '@lifi/widget-provider-tron';
import { register } from './core.jsx';

register('tron', () => [TronProvider()]);
