import { createRoot } from 'react-dom/client';
import { App } from './App';
import 'flag-icons/css/flag-icons.min.css';
import './styles.css';

const rootElement = document.getElementById('root');
if (!rootElement) {
  throw new Error('Root element not found.');
}

createRoot(rootElement).render(<App />);
